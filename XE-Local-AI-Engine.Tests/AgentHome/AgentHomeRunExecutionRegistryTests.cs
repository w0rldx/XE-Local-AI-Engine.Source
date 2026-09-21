namespace XE_Local_AI_Engine.Tests.AgentHome;

using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The per-run "is this run executing" signal the operator delete and the retention sweep read instead of the
///     sandbox-wide execution lease. Everything here is about one run id never answering for another.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class AgentHomeRunExecutionRegistryTests
{
    [Test]
    public void IsExecuting_ForARunThatWasNeverBegun_IsFalse()
    {
        AssertEx.False(new AgentHomeRunExecutionRegistry().IsExecuting("run-1758300000000-1"),
            "an empty registry must not claim a run is live, or every delete refuses.");
    }

    [Test]
    public void Begin_MarksOnlyThatRunAsExecuting()
    {
        var registry = new AgentHomeRunExecutionRegistry();

        using var scope = registry.Begin("run-1758300000000-1");

        AssertEx.True(registry.IsExecuting("run-1758300000000-1"));
        AssertEx.False(registry.IsExecuting("run-1758300000000-2"),
            "a live run must not answer for a finished one — that is the whole point of a per-run signal.");
    }

    [Test]
    public void Dispose_ClearsTheRegistration()
    {
        var registry = new AgentHomeRunExecutionRegistry();

        registry.Begin("run-1758300000000-1").Dispose();

        AssertEx.False(registry.IsExecuting("run-1758300000000-1"),
            "a registration that outlives its run makes that run's directory permanently undeletable.");
    }

    [Test]
    public void Dispose_Twice_DoesNotClearALaterRegistrationOfTheSameId()
    {
        var registry = new AgentHomeRunExecutionRegistry();
        var stale = registry.Begin("run-1758300000000-1");
        stale.Dispose();

        using var live = registry.Begin("run-1758300000000-1");
        stale.Dispose();

        AssertEx.True(registry.IsExecuting("run-1758300000000-1"),
            "a second dispose of a spent scope must be a no-op; ids are never re-minted, but the scope must not "
            + "depend on that to stay correct.");
    }

    [Test]
    public void Begin_WithSeveralRunsAtOnce_TracksEachIndependently()
    {
        var registry = new AgentHomeRunExecutionRegistry();
        var first = registry.Begin("run-1758300000000-1");
        using var second = registry.Begin("run-1758300000000-2");

        first.Dispose();

        AssertEx.False(registry.IsExecuting("run-1758300000000-1"));
        AssertEx.True(registry.IsExecuting("run-1758300000000-2"),
            "two owner-nodes can run at once under one runs root; one finishing must not release the other.");
    }

    [Test]
    public void IsExecuting_IsOrdinal()
    {
        var registry = new AgentHomeRunExecutionRegistry();

        using var scope = registry.Begin("run-1758300000000-1");

        AssertEx.False(registry.IsExecuting("RUN-1758300000000-1"),
            "run ids are compared byte for byte everywhere else; a case-insensitive match here would refuse a "
            + "delete for a run id that does not exist.");
    }
}
