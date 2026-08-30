namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The implementation lane: a <c>DevTask</c> node run driving a real Development task through the chain that
///     already exists, and what happens to the node run when that chain fails, is cancelled, or is not there at all.
///     <para>
///         Every test takes a host of its OWN: each scripts the development chain, whose stand-in is a container
///         singleton, and each seeds its own development project.
///     </para>
/// </summary>
public sealed class DevWorkflowDevTaskTests
{
    /// <summary>
    ///     The Slice B shape end to end: plan it, implement it, validate the result, and put it in front of a human.
    ///     <para>
    ///         The implementation node's success is the task reaching <c>AwaitingApply</c> — apply itself is a later act
    ///         behind the gate this graph ends on, which is the whole point of routing it through the workflow.
    ///     </para>
    /// </summary>
    private const string PlanImplementValidateReview = """
                                                       {
                                                         "schemaVersion": 1,
                                                         "nodes": [
                                                           { "nodeKey": "plan", "nodeType": "Agent", "label": "Plan",
                                                             "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                                           { "nodeKey": "implement", "nodeType": "DevTask", "label": "Implement" },
                                                           { "nodeKey": "validate", "nodeType": "Tool", "label": "Validate", "retryTarget": "implement" },
                                                           { "nodeKey": "review", "nodeType": "HumanGate", "label": "Human review" }
                                                         ],
                                                         "edges": [
                                                           { "from": "plan", "to": "implement" },
                                                           { "from": "implement", "to": "validate" },
                                                           { "from": "validate", "to": "review" }
                                                         ]
                                                       }
                                                       """;

    /// <summary>One implementation node on a five-second budget, with nothing left to try after it.</summary>
    private const string ImpatientDevTask = """
                                            {
                                              "schemaVersion": 1,
                                              "nodes": [{ "nodeKey": "implement", "nodeType": "DevTask", "label": "Implement", "maxAttempts": 1,
                                                          "nodeTimeoutSeconds": 5 }],
                                              "edges": []
                                            }
                                            """;

    /// <summary>One implementation node with one re-attempt in hand.</summary>
    private const string SingleDevTask = """
                                         {
                                           "schemaVersion": 1,
                                           "nodes": [{ "nodeKey": "implement", "nodeType": "DevTask", "label": "Implement", "maxAttempts": 2 }],
                                           "edges": []
                                         }
                                         """;

    /// <summary>
    ///     The Slice B6 gate: "Plan → Implement → Validate → Human review", with the implementation node driving a real
    ///     development task and the validation node's report in the run's own artifacts.
    /// </summary>
    [Test]
    public async Task ThePlanImplementValidateReviewChainRunsEndToEnd()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        var runId = await harness.StartRunAsync(PlanImplementValidateReview, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        _ = await harness.SaveAgentArtifactAsync(runId, "plan", "plan.md", "# Plan\n\nChange the thing.").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "plan").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var implemented = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, implemented.Status, AssertEx.NotNull(implemented.TerminalReason ?? implemented.OutputJson));
        AssertEx.Equal(taskId, implemented.DevelopmentTaskId, "the node run names the task it drove, which is how a reader drills into the evidence.");
        AssertEx.Contains(AssertEx.NotNull(implemented.OutputJson), "\"taskStatus\":\"AwaitingApply\"");
        AssertEx.Equal("Planned, Ready, InProgress, Validation, InReview",
            string.Join(", ", harness.Chain.Actions),
            "the node run drove the existing chain one stage at a time rather than forcing a status.");

        var task = await ReadTaskAsync(harness, taskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, task.Status, "and the task is left waiting to be applied: this node applies nothing.");

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false)).Status);
        AssertEx.Contains(await harness.ReadArtifactsAsync(runId).ConfigureAwait(false),
            artifact => artifact.Kind == DevWorkflowArtifactKind.ValidationReport,
            "the validation node's report is in the run's own artifacts, next to the plan the agent wrote.");

        var review = await harness.ReadNodeRunAsync(runId, "review").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, review.Status);
        AssertEx.Equal(DevWorkflowRunStatus.WaitingForApproval, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        await harness.DecideAsync(runId, "review", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply,
            (await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status,
            "approving the workflow's own gate is not applying the patch — that is a later act, behind its own gate.");
    }

    /// <summary>
    ///     A re-attempt KEEPS the task the previous one drove, rather than clearing the pointer.
    ///     <para>
    ///         The development schema allows one task per project, so there is no second task a re-attempt could point
    ///         at: the pointer names the task this node implements for the life of the run, and clearing it would only
    ///         take away the operator's link to the work while it is being retried. The plan's own §7.2 text says the
    ///         pointer is cleared and a new task created; the schema is the older and stronger fact.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AReAttemptKeepsThePointerToTheTaskItIsStillImplementing()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        harness.Chain.FailNextAttempts(1);
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var implemented = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(expected: 2, implemented.Attempt, "the failed development attempt cost the node run one of its own.");
        AssertEx.Equal(taskId, implemented.DevelopmentTaskId, "the pointer survived the re-attempt, because there is no other task it could name.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, implemented.Status, AssertEx.NotNull(implemented.TerminalReason ?? implemented.OutputJson));
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, (await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status);

        var retry = (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Single(static entry => entry.EventType == "node.retry.scheduled");
        AssertEx.Contains(AssertEx.NotNull(retry.DetailJson), "\"failureClass\":\"ProviderError\"");
    }

    /// <summary>
    ///     A development attempt that fails is the retry policy's to answer, exactly as a failed work session is: the
    ///     node re-attempts while it has attempts, and stands down for a human when it does not.
    /// </summary>
    [Test]
    public async Task ADevelopmentAttemptThatKeepsFailingStandsTheNodeRunDownForAHuman()
    {
        await using var harness = NewHarness();
        var (projectId, _) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        harness.Chain.FailNextAttempts(5);
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var blocked = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, blocked.Status);
        AssertEx.Equal(expected: 2, blocked.Attempt, "the node allowed two attempts, and both were spent on a real development attempt.");
        AssertEx.Equal(DevWorkflowFailureClasses.ProviderError, blocked.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(blocked.TerminalReason), "The scripted coder attempt failed.");
        AssertEx.Contains(AssertEx.NotNull(blocked.TerminalReason), "as many attempts as this node allows");
        AssertEx.Equal(DevWorkflowDecisionKind.Abandon, blocked.PendingDecisionKind);
        AssertEx.Equal(expected: 2, harness.Chain.Actions.Count, "each node-run attempt asked the chain for its own action rather than re-reading the last one's failure.");
        AssertEx.Equal(DevWorkflowWorkItemStatus.Blocked, (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     Cancelling a run stops the development attempt rather than abandoning it, and the row settles on what the
    ///     attempt actually did.
    /// </summary>
    [Test]
    public async Task CancellingARunStopsTheDevelopmentAttemptItWasDriving()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        harness.Chain.HoldNextAttempt();
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running,
            (await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false)).Status,
            "the chain is working, so the node run waits rather than asking it for more.");

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Cancelling).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, harness.Chain.CancelledAttempts.Count, "the development chain owns what stopping its work costs.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Cancelled, (await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevelopmentTaskStatus.InProgress,
            (await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status,
            "the task itself is left where it stood: a cancelled run abandons its own node run, not the operator's task.");
    }

    /// <summary>
    ///     A pause lets the attempt in flight finish and parks the node run, so the resume re-drives the same task from
    ///     wherever the chain left it.
    /// </summary>
    [Test]
    public async Task PausingARunLeavesTheDevelopmentAttemptToFinishAndParksTheNodeRun()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        harness.Chain.HoldNextAttempt();
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Pausing).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Empty(harness.Chain.CancelledAttempts, "a pause is meant to be resumed, so the attempt is left to finish rather than thrown away.");
        AssertEx.Equal(DevWorkflowRunStatus.Pausing,
            (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status,
            "and the run says so honestly until the attempt has landed.");

        // The attempt lands, and the pause can then complete.
        await LandTheHeldAttemptAsync(harness, taskId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var parked = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, parked.Status, "a paused implementation node waits rather than terminalizing.");
        AssertEx.Equal(taskId, parked.DevelopmentTaskId, "it keeps the task, which is what makes the resume a continuation.");
        AssertEx.Equal(DevWorkflowRunStatus.Paused, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Running).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, (await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A node with nothing to drive says so: Development Mode switched off is a configuration answer for a human,
    ///     not a failure to retry and not a container error inside a tick.
    /// </summary>
    [Test]
    public async Task ADevTaskNodeOnANodeWithDevelopmentModeOffStandsDown()
    {
        await using var harness = new DevWorkflowHarness(("Development:Enabled", "false"));
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", Guid.NewGuid()).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var blocked = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, blocked.Status);
        AssertEx.Equal(DevWorkflowFailureClasses.Configuration, blocked.FailureClass, "no retry answers a node that has nothing to run on.");
        AssertEx.Equal(expected: 1, blocked.Attempt, "and no attempt is spent finding that out.");
        AssertEx.Contains(AssertEx.NotNull(blocked.TerminalReason), "Development Mode is switched off");
    }

    /// <summary>
    ///     A node deadline reaches the development chain too: the attempt in flight is stopped and the node run ends on
    ///     the clock rather than on whatever that attempt would eventually have said.
    /// </summary>
    [Test]
    public async Task ADevTaskNodeRunPastItsDeadlineStopsTheAttemptAndEndsOnTheClock()
    {
        var clock = new ManualTimeProvider();
        await using var harness = NewHarness(clock);
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        harness.Chain.HoldNextAttempt();
        var runId = await harness.StartRunAsync(ImpatientDevTask, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false)).Status);

        clock.Advance(TimeSpan.FromMinutes(10));
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, harness.Chain.CancelledAttempts.Count, "the attempt is stopped rather than left running under a settled row.");
        var expired = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, expired.Status, "the node allowed one attempt, so a timeout has nowhere left to go but a human.");
        AssertEx.Equal(DevWorkflowFailureClasses.Timeout, expired.FailureClass, "the clock is why it ended, not the attempt it happened to cancel on the way.");
        AssertEx.Contains(AssertEx.NotNull(expired.TerminalReason), "5 seconds");
        AssertEx.Equal(taskId, expired.DevelopmentTaskId);
    }

    /// <summary>
    ///     A node run against a task the chain has ALREADY driven to <c>AwaitingApply</c> succeeds without asking it for
    ///     anything — the development state machine has no way back to <c>InProgress</c>, and the claim the node makes
    ///     is true either way. This is what a fix loop routed into an implementation node lands on.
    /// </summary>
    [Test]
    public async Task ANodeRunAgainstAnAlreadyImplementedTaskSucceedsWithoutDrivingIt()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        while ((await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status != DevelopmentTaskStatus.AwaitingApply)
        {
            _ = await harness.Chain.StartNextActionAsync(projectId, taskId, Guid.NewGuid()).ConfigureAwait(false);
        }

        var driven = harness.Chain.Actions.Count;
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var implemented = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, implemented.Status, AssertEx.NotNull(implemented.TerminalReason ?? implemented.OutputJson));
        AssertEx.Equal(expected: 1, implemented.Attempt);
        AssertEx.Contains(AssertEx.NotNull(implemented.OutputJson), "\"taskStatus\":\"AwaitingApply\"");
        AssertEx.Equal(driven, harness.Chain.Actions.Count, "the node run asked the chain for nothing: there was nothing left for it to do.");
    }

    /// <summary>The workflow host with the development chain scripted, and its own development project to drive.</summary>
    private static DevWorkflowHarness NewHarness(TimeProvider? clock = null) =>
        new(services =>
        {
            services.RemoveAll<IDevelopmentManagementService>();
            services.AddSingleton<IDevelopmentManagementService>(provider => new FakeDevelopmentTaskChain(provider.GetRequiredService<IServiceScopeFactory>()));
            if (clock is not null)
            {
                services.AddSingleton(clock);
            }
        });

    /// <summary>
    ///     A development project and the one task it owns, created the way Dev Mode creates them.
    ///     <para>
    ///         The selected folder is registered rather than invented: the project row has a foreign key to it. Its host
    ///         path is never opened, because nothing in these tests prepares a workspace — the chain that would is the
    ///         part they script.
    ///     </para>
    /// </summary>
    private static async Task<(Guid ProjectId, Guid TaskId)> SeedDevelopmentTaskAsync(DevWorkflowHarness harness)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var folder = await scope.ServiceProvider.GetRequiredService<ISelectedFolderResolver>()
                                .RegisterAsync(new SelectedFolderRegistration($"devtask-{Guid.NewGuid():N}"[..20],
                                    Path.Combine(Path.GetTempPath(), $"xe-devtask-{Guid.NewGuid():N}")))
                                .ConfigureAwait(false);

        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        _ = await scope.ServiceProvider.GetRequiredService<IDevelopmentStore>()
                       .CreateProjectAsync(new DevelopmentCreateProjectCommand(projectId,
                           taskId,
                           Guid.NewGuid(),
                           "Keep the product working.",
                           Guid.Parse(folder.Id),
                           "repository-identity-hash",
                           "main",
                           "Add the feature",
                           "It has to do the thing.",
                           "[\"it does the thing\"]"))
                       .ConfigureAwait(false);
        return (projectId, taskId);
    }

    private static async Task<DevelopmentTaskSnapshot> ReadTaskAsync(DevWorkflowHarness harness, Guid taskId)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDevelopmentStore>().GetTaskAsync(taskId).ConfigureAwait(false);
    }

    /// <summary>Lands the held attempt the way its runner would have, so the drain has nothing left to wait for.</summary>
    private static async Task LandTheHeldAttemptAsync(DevWorkflowHarness harness, Guid taskId)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var attempts = await store.ListAttemptsAsync(taskId).ConfigureAwait(false);
        var attempt = attempts[^1];
        _ = await store.TerminalizeAttemptAsync(new DevelopmentTerminalizeAttemptCommand(attempt.Id,
                               Guid.NewGuid(),
                               DevelopmentAttemptStatus.Succeeded,
                               attempt.Version))
                       .ConfigureAwait(false);
    }
}
