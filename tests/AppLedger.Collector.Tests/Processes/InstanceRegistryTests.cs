using AppLedger.Collector.Processes;
using AppLedger.Collector.Tests.TestSupport;
using AppLedger.Core.Identity;
using Shouldly;
using Xunit;

namespace AppLedger.Collector.Tests.Processes;

/// <summary>
/// What the registry hands the resolver, which decides whether identity can work at all.
/// </summary>
/// <remarks>
/// Every case here is silent when it is wrong. A child resolved before its parent is not an error, it is a
/// process that quietly becomes its own app; a missing parent key is not an error, it is adoption that never
/// fires; and a re-resolution built from the wrong facts is not an error either, it is every app on the
/// machine turning into a <c>root:</c> hash. All three were real (docs/24_ADR.md §Findings, 2026-10-30).
/// </remarks>
public sealed class InstanceRegistryTests
{
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    /// <summary>
    /// Adoption reads the parent's answer out of the resolver's cache, so the parent has to be resolved
    /// first. <c>NtQuerySystemInformation</c> returns no particular order, and on the first tick every live
    /// process arrives as a start — the whole tree at once.
    /// </summary>
    [Fact]
    public void Apply_ResolvesStartsInCreateTimeOrder()
    {
        var resolver = new RecordingResolver();
        var registry = Build(resolver);

        // Deliberately shuffled, and the child of the three listed first.
        registry.Apply(Tick(
            Start(300, 3000, "child.exe", parentPid: 100),
            Start(100, 1000, "parent.exe"),
            Start(200, 2000, "middle.exe")));

        resolver.Seen.Select(f => f.Key.CreateTime).ShouldBe([1000L, 2000L, 3000L]);
    }

    /// <summary>
    /// A snapshot carries only the parent's pid. Turning that into a <c>(pid, createTime)</c> key is what
    /// makes adoption possible, and it was simply not being done.
    /// </summary>
    [Fact]
    public void Apply_PassesTheParentInstanceKey()
    {
        var resolver = new RecordingResolver();
        var registry = Build(resolver);

        registry.Apply(Tick(Start(100, 1000, "parent.exe")));
        registry.Apply(Tick(Start(300, 3000, "child.exe", parentPid: 100)));

        resolver.Seen[^1].Parent.ShouldBe(new ProcessKey(100, 1000));
    }

    /// <summary>
    /// The PID-reuse guard, at the point where the pid is turned into a key. The parent exited and an
    /// unrelated process took its number; that process is younger than the child, so it is not its parent
    /// and the child must resolve on its own.
    /// </summary>
    [Fact]
    public void Apply_WhenAnUnrelatedProcessHasTakenTheParentPid_PassesNoParent()
    {
        var resolver = new RecordingResolver();
        var registry = Build(resolver);

        registry.Apply(Tick(Start(100, 1000, "parent.exe")));

        // The parent exits, its pid is recycled by something newer, and a child that names pid 100 starts.
        registry.Apply(new ProcessTick(
            TsUtc: 2,
            Deltas: [],
            Started: [Start(100, 9000, "unrelated.exe"), Start(300, 3000, "child.exe", parentPid: 100)],
            Exited: [Start(100, 1000, "parent.exe")],
            Rebaselined: false));

        var child = resolver.Seen.Single(f => f.Key.Pid == 300);
        child.Parent.ShouldBeNull();
    }

    /// <summary>
    /// A re-resolution has to ask the same question the first one did. An earlier version rebuilt the facts
    /// from the <i>resolution</i> — display name as the image name, install root as the image path — which
    /// is invisible with a resolver that reads only those two and destroys grouping with one that matches on
    /// executable names, signers and command lines.
    /// </summary>
    [Fact]
    public void InvalidateResolutions_ReResolvesFromTheFactsItWasFirstGiven()
    {
        var resolver = new RecordingResolver();
        var registry = Build(resolver, (new ProcessKey(100, 1000), Chrome));

        registry.Apply(Tick(Start(100, 1000, "chrome.exe")));
        var first = resolver.Seen.Single();

        registry.InvalidateResolutions();

        resolver.Invalidations.ShouldBe(1);
        resolver.Seen.Count.ShouldBe(2);

        var second = resolver.Seen[^1];
        second.ImageFileName.ShouldBe("chrome.exe", "the image name, not the app's display name");
        second.ImagePath.ShouldBe(Chrome, "the image path, not the install root");
        second.Key.ShouldBe(first.Key);
    }

    private static InstanceRegistry Build(
        IIdentityResolver resolver, params (ProcessKey Key, string Path)[] imagePaths)
    {
        var enricher = new FakeProcessEnricher();
        foreach (var (key, path) in imagePaths)
        {
            enricher.WithImagePath(key, path);
        }

        return new InstanceRegistry(new FakePolicyGuard(), enricher, resolver);
    }

    private static ProcessTick Tick(params ProcessLifecycleEvent[] started) =>
        new(TsUtc: 1, Deltas: [], Started: started, Exited: [], Rebaselined: false);

    private static ProcessLifecycleEvent Start(int pid, long createTime, string image, int parentPid = 0) =>
        new(new ProcessKey(pid, createTime), image, parentPid);

    /// <summary>A resolver that answers uselessly but records exactly what it was asked.</summary>
    private sealed class RecordingResolver : IIdentityResolver
    {
        public List<ProcessFacts> Seen { get; } = [];

        public int Invalidations { get; private set; }

        public ResolutionResult Resolve(ProcessFacts facts)
        {
            Seen.Add(facts);

            return new ResolutionResult
            {
                AppId = AppId.Root(facts.ImageFileName),
                Source = AppSource.Root,
                Confidence = Confidence.RootFallback,
                DecidedBy = ResolutionStep.RootFallback,
                DisplayName = "Display Name " + facts.ImageFileName,
                InstallRoot = @"C:\Program Files\Google",
            };
        }

        public void Invalidate() => Invalidations++;
    }
}
