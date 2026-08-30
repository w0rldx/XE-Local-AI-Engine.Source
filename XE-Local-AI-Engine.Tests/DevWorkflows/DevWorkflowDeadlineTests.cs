namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Node deadlines: a node run that has been going longer than its node allows is ended by the run itself, and the
///     deadline is derived from the row rather than armed in memory — which is what a pause and a restart both rely on.
///     <para>
///         Every test here takes a host of its OWN, because each one replaces the clock: a shared host's clock is every
///         sibling's clock, and advancing it half an hour would expire their node runs too.
///     </para>
/// </summary>
public sealed class DevWorkflowDeadlineTests
{
    /// <summary>A project id on the work item, because a graph with tool nodes in it is only startable with one.</summary>
    private static readonly Guid DevelopmentProjectId = Guid.NewGuid();

    /// <summary>
    ///     Comfortably past the 30-second grace the row's deadline allows the lane to answer its own budget in. A test
    ///     clock, so the wait costs a method call.
    /// </summary>
    private static readonly TimeSpan PastTheDeadline = TimeSpan.FromMinutes(10);

    /// <summary>One tool node with a five-second budget and one re-attempt in hand.</summary>
    private const string ImpatientToolGraph = """
                                              {
                                                "schemaVersion": 1,
                                                "nodes": [{ "nodeKey": "validate", "nodeType": "Tool", "nodeTimeoutSeconds": 5, "maxAttempts": 2 }],
                                                "edges": []
                                              }
                                              """;

    /// <summary>One agent node with a five-second budget and nothing left to try after it.</summary>
    private const string ImpatientAgentGraph = """
                                               {
                                                 "schemaVersion": 1,
                                                 "nodes": [{ "nodeKey": "research", "nodeType": "Agent", "label": "Research", "maxAttempts": 1,
                                                             "nodeTimeoutSeconds": 5,
                                                             "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" }],
                                                 "edges": []
                                               }
                                               """;

    /// <summary>
    ///     A sandbox pass that stops answering for itself is ended by the run: the row fails on the clock, the pass is
    ///     dropped rather than left holding a slot, and the node gets the attempt its definition still allows it.
    /// </summary>
    [Test]
    public async Task AToolNodeRunPastItsDeadlineIsEndedByTheRunAndTriedAgain()
    {
        var clock = new ManualTimeProvider();
        await using var harness = new DevWorkflowHarness(services => services.AddSingleton<TimeProvider>(clock));
        var held = harness.Tools.Hold("validate");
        var runId = await harness.StartRunAsync(ImpatientToolGraph, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await held.Started.ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false)).Status);

        clock.Advance(PastTheDeadline);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        var expired = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(expected: 2, expired.Attempt, "a timeout is retryable, and this node had an attempt left.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, expired.Status, "the same tick that ended the first attempt admits the second: it waits on no clock.");
        AssertEx.Equal(expected: 2, harness.Tools.Ran.Count, "and the lane really did start the second attempt's commands.");

        var scheduled = (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Last(static entry => entry.EventType == "node.retry.scheduled");
        AssertEx.Equal("timeout", scheduled.Outcome, "the log says the clock ended it, which the row no longer can once it is re-attempted.");
        AssertEx.Contains(AssertEx.NotNull(scheduled.DetailJson), "\"failureClass\":\"Timeout\"");
        AssertEx.Contains(AssertEx.NotNull(scheduled.DetailJson), "5 seconds", message: "the reason names the budget it went past.");

        // The second attempt settles on its OWN pass. It can only do that if the first attempt's entry was dropped from
        // the registry rather than merely cancelled: an entry left behind refuses the fresh pass its place, and the next
        // poll then finds nothing driving the row and settles it as an interrupted host instead.
        held.Release();
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);
        var settled = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, settled.Status, AssertEx.NotNull(settled.TerminalReason ?? settled.OutputJson));
        AssertEx.Equal("validate, validate", string.Join(", ", harness.Tools.Ran));
    }

    /// <summary>
    ///     The lane with no budget of its own: an agent node run whose session never lands is stopped and stood down,
    ///     which is the bound nothing else in the runtime provides.
    /// </summary>
    [Test]
    public async Task AnAgentNodeRunPastItsDeadlineHasItsSessionStoppedAndStandsDown()
    {
        var clock = new ManualTimeProvider();
        await using var harness = new DevWorkflowHarness(services => services.AddSingleton<TimeProvider>(clock));
        var runId = await harness.StartRunAsync(ImpatientAgentGraph).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var sessionId = await harness.ReadSessionIdAsync(runId, "research").ConfigureAwait(false);

        clock.Advance(PastTheDeadline);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Contains(harness.Agent.Calls, call => call == ("cancel", sessionId), "the session is stopped, not abandoned to run on under a settled row.");

        var expired = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, expired.Status, "the node allowed one attempt, so a timeout has nowhere left to go but a human.");
        AssertEx.Equal(DevWorkflowFailureClasses.Timeout, expired.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(expired.TerminalReason), "5 seconds");
        AssertEx.Contains(AssertEx.NotNull(expired.OutputJson), "\"failureClass\":\"Timeout\"");
    }

    /// <summary>
    ///     Walkthrough #11: a run paused across a long outage must not find every node instantly out of time. Nothing
    ///     clears a deadline explicitly — a paused run holds no <em>running</em> row, and the row a pause parks has had
    ///     its start instant cleared, so the resume's re-admission is what starts the clock again.
    /// </summary>
    [Test]
    public async Task APausedRunHoldsNoDeadlineAndItsResumeStartsTheClockAgain()
    {
        var clock = new ManualTimeProvider();
        await using var harness = new DevWorkflowHarness(services => services.AddSingleton<TimeProvider>(clock));
        var runId = await harness.StartRunAsync(ImpatientAgentGraph).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var sessionId = await harness.ReadSessionIdAsync(runId, "research").ConfigureAwait(false);

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Pausing).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Paused, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        var parked = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Null(parked.StartedAtUtc, "the parked row carries no start instant, which is exactly what leaves it no deadline to expire.");

        // The outage: far longer than the node's five seconds, and longer than the grace on top of them.
        clock.Advance(PastTheDeadline);
        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Running).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var resumed = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, resumed.Status, "the resumed node run is working, not instantly out of time.");
        AssertEx.Equal(expected: 1, resumed.Attempt, "and it is the same attempt: a pause costs no attempt.");
        AssertEx.Equal(sessionId, resumed.WorkSessionId);
        AssertEx.True(resumed.StartedAtUtc > parked.CreatedAtUtc, "the resume re-based the deadline by stamping a fresh start instant.");

        // One more tick at the same instant: the re-based deadline has not passed, so nothing ends the node run.
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A node that declares no timeout has none. The defaults §8.2 names for sandbox work are the DEVELOPMENT
    ///     attempt budget, which the lane below applies to the work it can see; deriving a second number up here could
    ///     only ever disagree with it.
    /// </summary>
    [Test]
    public async Task ANodeThatDeclaresNoTimeoutIsNotEndedByTheRun()
    {
        var clock = new ManualTimeProvider();
        await using var harness = new DevWorkflowHarness(services => services.AddSingleton<TimeProvider>(clock));
        var held = harness.Tools.Hold("validate");
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.SingleTool, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await held.Started.ConfigureAwait(false);

        clock.Advance(TimeSpan.FromDays(1));
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        var running = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, running.Status);
        AssertEx.True(harness.ToolLane.IsInFlight(running.Id), "the pass is still the lane's, because nothing on this node run was ever due.");

        held.Release();
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false)).Status);
    }
}
