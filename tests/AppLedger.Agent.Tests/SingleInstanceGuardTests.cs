using Shouldly;
using Xunit;

namespace AppLedger.Agent.Tests;

/// <summary>
/// The single-instance guard in <c>Program.ServeAsync</c> (docs/16_PACKAGING_AND_UPDATES.md §Scheduled Task).
/// </summary>
/// <remarks>
/// Three facts about the primitive, and together they are the whole reason the guard asks
/// <c>CreateMutex</c> whether it created the object rather than acquiring it. The first is a defect that
/// shipped: the obvious shape costs nothing until the Agent shuts down cleanly, and then it throws.
/// <c>--serve</c> itself needs elevation and a real collector, so none of this is reachable through the
/// Agent from a test - but the primitive underneath it is, and that is what is pinned here.
/// </remarks>
public sealed class SingleInstanceGuardTests
{
    /// <summary>
    /// <c>Mutex</c> ownership is thread-affine, so <c>WaitOne</c> on the way in and <c>ReleaseMutex</c> in a
    /// <c>finally</c> cannot work in an <c>async</c> method: the finally runs after
    /// <c>ConfigureAwait(false)</c> has resumed on a thread-pool thread, so the release throws out of the
    /// finally and replaces the return value. Every clean <c>--serve</c> shutdown would have ended in an
    /// unhandled exception and a non-zero exit code, which Task Scheduler records as a failed task
    /// (docs/24_ADR.md §Findings).
    /// </summary>
    [Fact]
    public void ReleaseMutex_FromAThreadThatDoesNotOwnIt_Throws()
    {
        var name = Unique();
        using var mutex = new Mutex(initiallyOwned: false, name, out var createdNew);

        createdNew.ShouldBeTrue("a uniquely named object is new by construction");
        mutex.WaitOne(TimeSpan.Zero).ShouldBeTrue("the entry thread takes ownership");

        // A dedicated thread rather than Task.Run: the thread pool is allowed to hand the work back to
        // this very thread, and then the test would pass for the wrong reason.
        Exception? thrown = null;
        var other = new Thread(() => thrown = Record.Exception(mutex.ReleaseMutex));
        other.Start();
        other.Join();

        thrown.ShouldBeOfType<ApplicationException>(
            "ownership is thread-affine, which is what an await between WaitOne and ReleaseMutex breaks");

        // Leave nothing abandoned: release it from the thread that actually owns it.
        mutex.ReleaseMutex();
    }

    /// <summary>
    /// What the guard relies on instead. Whoever creates the named object is the single instance, decided
    /// atomically by the kernel, with no thread owning anything and so nothing to release.
    /// </summary>
    [Fact]
    public void CreateMutex_ASecondHandleToTheSameName_IsNotTheCreator()
    {
        var name = Unique();

        using var first = new Mutex(initiallyOwned: false, name, out var firstCreated);
        using var second = new Mutex(initiallyOwned: false, name, out var secondCreated);

        firstCreated.ShouldBeTrue("the first handle creates the object");
        secondCreated.ShouldBeFalse("the second one finds it, which is what 'already running' means");
    }

    /// <summary>
    /// And it is self-healing, which matters because an elevated collector is exactly the kind of process
    /// that gets killed rather than asked to stop: the object exists only while a handle does, so an Agent
    /// that died without releasing anything leaves nothing for the next start to trip over.
    /// </summary>
    [Fact]
    public void CreateMutex_AfterEveryHandleIsClosed_CreatesTheObjectAgain()
    {
        var name = Unique();

        using (new Mutex(initiallyOwned: false, name, out var created))
        {
            created.ShouldBeTrue();
        }

        using var again = new Mutex(initiallyOwned: false, name, out var createdAgain);

        createdAgain.ShouldBeTrue("a closed handle leaves no object behind");
    }

    /// <summary>A name no other test and no running Agent can collide with.</summary>
    private static string Unique() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"AppLedgerTest-{Guid.NewGuid():N}");
}
