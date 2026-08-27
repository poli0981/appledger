using AppLedger.Core.Policy;

namespace AppLedger.Core.Identity;

/// <summary>
/// The v0.2 stand-in for the real resolver: enough identity to group processes into apps and fill a
/// metrics table, and nothing more.
/// </summary>
/// <remarks>
/// <b>This is deliberately not the resolver docs/03_APP_IDENTITY.md specifies.</b> It has no catalog rules,
/// no host rules, no launcher manifests, no MSIX lookup and no parent adoption. It resolves a Tier-0 image
/// to the <c>sys:*</c> family and everything else to <c>root:&lt;hash of install root&gt;</c>, which is the
/// last two steps of that pipeline with the middle eight missing.
/// <para>
/// It exists because v0.2's scope includes an apps list, and an apps list needs app ids; the real resolver
/// is gated by spike S2 at v0.3 and lands with its fixture suite rather than ahead of it. Every result here
/// carries the confidence of the step that produced it, so the UI's "?" badge already tells the truth: most
/// of these are 0.30 root fallbacks, and they should look like it.
/// </para>
/// </remarks>
public sealed class FallbackIdentityResolver : IIdentityResolver
{
    private readonly IPolicyGuard _policy;
    private readonly InstallRootHeuristic _installRoots;

    /// <summary>Creates a resolver over the policy and the install-root heuristic.</summary>
    public FallbackIdentityResolver(IPolicyGuard policy, InstallRootHeuristic installRoots)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(installRoots);
        _policy = policy;
        _installRoots = installRoots;
    }

    /// <inheritdoc />
    public ResolutionResult Resolve(ProcessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        // The two processes that exist before anything else does and have no image of their own.
        if (facts.Key.Pid == 0)
        {
            return System(AppId.Idle, "Idle");
        }

        if (facts.Key.Pid == 4)
        {
            return System(AppId.SystemProcess, "System");
        }

        // Step 1 of docs/03's pipeline: anything under a Tier-0 root is a Windows component, decided from
        // the path alone and without opening anything.
        if (facts.ImagePath is not null && _policy.TierOf(facts.ImagePath) == PathTier.ProtectedOs)
        {
            return System(AppId.Windows, DisplayNameFor(facts));
        }

        var installRoot = _installRoots.FromImagePath(facts.ImagePath);
        if (installRoot is not null)
        {
            // Step 10: the install-root hash fallback. Confidence 0.30 per docs/03 §Confidence, which is
            // below the prompt threshold, so the UI offers "Assign to app..." exactly as it should.
            return new ResolutionResult
            {
                AppId = AppId.Root(installRoot),
                Source = AppSource.Root,
                Confidence = Confidence.RootFallback,
                DecidedBy = ResolutionStep.RootFallback,
                DisplayName = DisplayNameFor(facts),
                InstallRoot = installRoot,
                Evidence = [new IdentityEvidence(ResolutionStep.RootFallback, "install root")],
            };
        }

        // No path at all: a Tier-2 process we never opened, or one that exited before enrichment ran. The
        // image name is the only thing left, and it is better than dropping the process entirely.
        return new ResolutionResult
        {
            AppId = AppId.Root(facts.ImageFileName),
            Source = AppSource.Root,
            Confidence = Confidence.RootFallback,
            DecidedBy = ResolutionStep.RootFallback,
            DisplayName = DisplayNameFor(facts),
            Evidence = [new IdentityEvidence(ResolutionStep.RootFallback, "image name only")],
        };
    }

    /// <inheritdoc />
    /// <remarks>Nothing is cached here, so there is nothing to invalidate. The caller's cache is.</remarks>
    public void Invalidate()
    {
    }

    private static ResolutionResult System(AppId appId, string displayName) => new()
    {
        AppId = appId,
        Source = AppSource.System,
        Confidence = Confidence.Certain,
        DecidedBy = ResolutionStep.ProtectedOsPath,
        DisplayName = displayName,
        Evidence = [new IdentityEvidence(ResolutionStep.ProtectedOsPath, "protected OS path")],
    };

    /// <summary>
    /// The friendliest name the facts carry. <c>FileDescription</c> is what Task Manager shows, and it is
    /// the only one of these that is written for a human to read.
    /// </summary>
    private static string DisplayNameFor(ProcessFacts facts) =>
        FirstNonEmpty(facts.FileDescription, facts.ProductName, facts.ImageFileName) ?? "(unknown)";

    private static string? FirstNonEmpty(params string?[] candidates) =>
        Array.Find(candidates, c => !string.IsNullOrWhiteSpace(c))?.Trim();
}
