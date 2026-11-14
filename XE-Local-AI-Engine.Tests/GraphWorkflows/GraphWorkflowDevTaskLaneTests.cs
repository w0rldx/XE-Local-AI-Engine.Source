namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The <c>DevTask</c> lane (<c>GraphWorkflowDevTaskExecutor</c>): <c>implement</c> creates, pins and drives one Development task,
///     <c>apply</c> applies it on behalf of its run, a restart resumes the pinned task, and Development Mode off fails the node.
/// </summary>
/// <remarks>
///     Every test takes a host of its own: the scripted chain is a container singleton each test scripts. The guard tests run
///     the REAL management service, because an ownership guard asserted against a stand-in is not asserted at all.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowDevTaskLaneTests
{
    private const string ImplementOnly = """
                                         { "schemaVersion": 1,
                                           "nodes": [{ "key": "start", "kind": "Start" },
                                                     { "key": "impl", "kind": "DevTask",
                                                       "config": { "action": "implement", "title": "Fix login", "requirements": "Fix the login bug.",
                                                                   "acceptanceCriteria": ["Login works"], "instructions": "Read the request first." } },
                                                     { "key": "done", "kind": "End", "config": { "outcome": "completed" } }],
                                           "edges": [{ "key": "e1", "from": "start", "to": "impl" }, { "key": "e2", "from": "impl", "to": "done" }] }
                                         """;

    private const string ImplementReviewApply = """
                                                { "schemaVersion": 1,
                                                  "nodes": [{ "key": "start", "kind": "Start" },
                                                            { "key": "impl", "kind": "DevTask", "config": { "action": "implement", "requirements": "Fix the login bug." } },
                                                            { "key": "review", "kind": "Pause", "config": { "prompt": "Apply the patch?", "allowedDecisions": ["Approve", "Reject"] } },
                                                            { "key": "apply", "kind": "DevTask", "config": { "action": "apply", "task": "impl" } },
                                                            { "key": "shipped", "kind": "End", "config": { "outcome": "shipped" } },
                                                            { "key": "declined", "kind": "End", "config": { "outcome": "declined" } }],
                                                  "edges": [{ "key": "e1", "from": "start", "to": "impl" },
                                                            { "key": "e2", "from": "impl", "to": "review" },
                                                            { "key": "e3", "from": "review", "to": "apply", "condition": { "path": "output.decision", "op": "Eq", "value": "Approve" } },
                                                            { "key": "e4", "from": "review", "to": "declined", "condition": { "path": "output.decision", "op": "Eq", "value": "Reject" } },
                                                            { "key": "e5", "from": "apply", "to": "shipped" }] }
                                                """;

    // DEVTASK-1 accepts this: impl is an ancestor of the review. With joinPolicy Any the review is also reached from start, past impl.
    private const string AnyJoinReviewApply = """
                                              { "schemaVersion": 1,
                                                "nodes": [{ "key": "start", "kind": "Start" },
                                                          { "key": "impl", "kind": "DevTask", "config": { "action": "implement", "requirements": "Fix the login bug." } },
                                                          { "key": "review", "kind": "Pause", "joinPolicy": "Any", "config": { "prompt": "Apply the patch?", "allowedDecisions": ["Approve", "Reject"] } },
                                                          { "key": "apply", "kind": "DevTask", "config": { "action": "apply", "task": "impl" } },
                                                          { "key": "shipped", "kind": "End", "config": { "outcome": "shipped" } },
                                                          { "key": "declined", "kind": "End", "config": { "outcome": "declined" } }],
                                                "edges": [{ "key": "e1", "from": "start", "to": "impl" },
                                                          { "key": "e2", "from": "start", "to": "review" },
                                                          { "key": "e3", "from": "impl", "to": "review" },
                                                          { "key": "e4", "from": "review", "to": "apply", "condition": { "path": "output.decision", "op": "Eq", "value": "Approve" } },
                                                          { "key": "e5", "from": "review", "to": "declined", "condition": { "path": "output.decision", "op": "Eq", "value": "Reject" } },
                                                          { "key": "e6", "from": "apply", "to": "shipped" }] }
                                              """;

    /// <summary>
    ///     The first dispatch creates ONE task from the node's config, keyed on run and node, and pins it on the row before Dev
    ///     Mode is asked for anything; the node then drives the chain to <c>AwaitingApply</c> and succeeds naming the task.
    /// </summary>
    [Test]
    public async Task Implement_CreatesOneTaskAndPinsItBeforeDrivingItToAwaitingApply()
    {
        using var folder = new TempDirectory("xe-devtask-lane");
        await using var harness = ChainHost(folder.Path);
        var projectId = await SeedProjectAsync(harness, folder.Path);
        var seeded = (await ListTasksAsync(harness, projectId)).Single();
        var runId = await harness.StartRunAsync(ImplementOnly, Input(projectId));
        var ownedAtEachAsk = new List<bool>();
        Chain(harness).BeforeNextAction = async taskId =>
        {
            await using var scope = harness.Services.CreateAsyncScope();
            var owners = await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().ListDevelopmentTaskRunsAsync([taskId]);
            ownedAtEachAsk.Add(owners.Any(owner => owner.RunId == runId));
        };

        _ = await harness.AdvanceUntilQuiescentAsync(runId, maxTicks: 40);

        var implemented = await harness.ReadNodeRunAsync(runId, "impl");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, implemented.Status, implemented.Error ?? implemented.OutputJson);
        var taskId = TaskOf(implemented);
        AssertEx.False(taskId == seeded.Id, "the node creates its own task rather than taking the project's first one.");
        AssertEx.Equal(expected: 2, (await ListTasksAsync(harness, projectId)).Count, "exactly one task was created.");
        AssertEx.Equal("Planned, Ready, InProgress, Validation, InReview", string.Join(", ", Chain(harness).Actions), "the chain was driven one stage per ask.");
        AssertEx.True(ownedAtEachAsk.Count == 5 && ownedAtEachAsk.All(static owned => owned),
            $"the pin was durable before EVERY ask, the first included: {string.Join(", ", ownedAtEachAsk)}");

        var task = await ReadTaskAsync(harness, taskId);
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, task.Status, "implement applies nothing.");
        AssertEx.Equal("Fix login", task.Title);
        AssertEx.Equal("Fix the login bug.", task.Requirements);
        AssertEx.Contains(task.AcceptanceCriteriaJson, "Login works");
        AssertEx.Contains(AssertEx.NotNull(implemented.OutputJson), $"\"developmentTaskId\":\"{taskId:D}\"");
        AssertEx.Contains(AssertEx.NotNull(implemented.OutputJson), "\"taskStatus\":\"AwaitingApply\"");
        AssertEx.Equal(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId)).Status);
    }

    /// <summary>
    ///     The coder reads requirements, bound data and criteria as written: backticks and non-ASCII verbatim, never as
    ///     <c>\uXXXX</c>, and the bound value parses back to exactly the text the run carried.
    /// </summary>
    [Test]
    public async Task Implement_HandsTheCoderBackticksQuotesAndNonAsciiAsWritten()
    {
        const string written = "Run `make test` and print \"Grüße — ✓\" <done> & 'ok'.";
        using var folder = new TempDirectory("xe-devtask-lane");
        await using var harness = ChainHost(folder.Path);
        var projectId = await SeedProjectAsync(harness, folder.Path);
        var graph = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            nodes = new object[]
            {
                new
                {
                    key = "start",
                    kind = "Start"
                },
                new
                {
                    key = "impl",
                    kind = "DevTask",
                    config = new
                    {
                        action = "implement",
                        requirements = written,
                        acceptanceCriteria = new[]
                        {
                            written
                        },
                        inputBindings = new
                        {
                            plan = "run.input.plan"
                        }
                    }
                },
                new
                {
                    key = "done",
                    kind = "End",
                    config = new
                    {
                        outcome = "completed"
                    }
                }
            },
            edges = new[]
            {
                new
                {
                    key = "e1",
                    from = "start",
                    to = "impl"
                },
                new
                {
                    key = "e2",
                    from = "impl",
                    to = "done"
                }
            }
        });
        var runId = await harness.StartRunAsync(graph, JsonSerializer.Serialize(new
        {
            developmentProjectId = projectId,
            plan = written
        }));

        _ = await harness.AdvanceUntilQuiescentAsync(runId, maxTicks: 40);

        var task = await ReadTaskAsync(harness, TaskOf(await harness.ReadNodeRunAsync(runId, "impl")));
        AssertEx.True(task.Requirements.StartsWith(written, StringComparison.Ordinal), $"the requirements text arrives as written: {task.Requirements}");
        AssertEx.False(task.Requirements.Contains("\\u", StringComparison.Ordinal), $"nothing reaches the coder as a \\u escape: {task.Requirements}");
        AssertEx.Contains(task.Requirements, "\"plan\":\"Run `make test` and print \\\"Grüße — ✓\\\" <done> & 'ok'.\"", message: "the bound value keeps every character but the JSON quote escape.");
        AssertEx.False(task.AcceptanceCriteriaJson.Contains("\\u", StringComparison.Ordinal), $"nor do the criteria: {task.AcceptanceCriteriaJson}");
        AssertEx.Equal(written, JsonSerializer.Deserialize<string[]>(task.AcceptanceCriteriaJson)?.Single());
    }

    /// <summary>
    ///     A host restart mid-implement: Dev Mode's reconciler interrupts the attempt, Graph's collapses the row to <c>Pending</c>
    ///     with its pin and attempt kept, and the re-dispatch resumes the SAME task without creating a second one.
    /// </summary>
    [Test]
    public async Task ARestartMidImplement_ResumesThePinnedTaskWithoutCreatingAnother()
    {
        using var folder = new TempDirectory("xe-devtask-lane");
        await using var harness = ChainHost(folder.Path);
        var projectId = await SeedProjectAsync(harness, folder.Path);
        Chain(harness).HoldNextAttempt();
        var runId = await harness.StartRunAsync(ImplementOnly, Input(projectId));
        await harness.AdvanceUntilAsync(runId,
            async () => (await harness.ReadNodeRunAsync(runId, "impl")).DevelopmentTaskId is { } driven
                        && (await ListAttemptsAsync(harness, driven)).Any(static attempt => attempt.Status == DevelopmentAttemptStatus.Running),
            "the node never had a coder attempt running");
        var pinned = TaskOf(await harness.ReadNodeRunAsync(runId, "impl"));

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            _ = await scope.ServiceProvider.GetRequiredService<IDevelopmentStore>().ReconcileRunningAttemptsAsync("The host restarted.");
            await new GraphWorkflowStartupReconciler(harness.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<GraphWorkflowStartupReconciler>.Instance)
                .RecoverAsync(scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>(), CancellationToken.None);
        }

        var collapsed = await harness.ReadNodeRunAsync(runId, "impl");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Pending, collapsed.Status, "a DevTask row resumes rather than failing Interrupted.");
        AssertEx.Equal(pinned, collapsed.DevelopmentTaskId, "the collapse keeps the pin.");
        AssertEx.Equal(expected: 1, collapsed.Attempt, "and costs no attempt.");

        _ = harness.CreateReplacementDispatcher();
        _ = await harness.AdvanceUntilQuiescentAsync(runId, maxTicks: 40);

        var resumed = await harness.ReadNodeRunAsync(runId, "impl");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, resumed.Status, resumed.Error ?? resumed.OutputJson);
        AssertEx.Equal(pinned, resumed.DevelopmentTaskId, "the resumed row drove the task it had pinned.");
        AssertEx.Equal(expected: 2, (await ListTasksAsync(harness, projectId)).Count, "no second task was created.");
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, (await ReadTaskAsync(harness, pinned)).Status);
    }

    /// <summary>A coder attempt that fails is the node's failure, classified like any other: one attempt, so <c>AttemptsExhausted</c>.</summary>
    [Test]
    public async Task AnImplementAttemptThatFails_FailsTheNodeAndTheRun()
    {
        using var folder = new TempDirectory("xe-devtask-lane");
        await using var harness = ChainHost(folder.Path);
        var projectId = await SeedProjectAsync(harness, folder.Path);
        Chain(harness).FailNextAttempts(1);
        var runId = await harness.StartRunAsync(ImplementOnly, Input(projectId));

        _ = await harness.AdvanceUntilQuiescentAsync(runId, maxTicks: 40);

        var failed = await harness.ReadNodeRunAsync(runId, "impl");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, failed.Status, failed.OutputJson);
        AssertEx.Equal(GraphWorkflowFailureClass.AttemptsExhausted, failed.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(failed.Error), "The scripted coder attempt failed.");
        AssertEx.Equal(GraphWorkflowRunStatus.Failed, (await harness.ReadRunAsync(runId)).Status);
    }

    /// <summary>
    ///     ADR 0023 decision 7 on the REAL management service: a missing test runner stands the task down and fails the run; the
    ///     Development page's next action re-runs that validation once the run has ended, and is refused while it is live.
    /// </summary>
    [Test]
    public async Task ATaskAFailedImplementLeftBlocked_IsContinuedFromTheDevelopmentPageOnceTheRunHasEnded()
    {
        using var folder = new TempDirectory("xe-devtask-lane");
        await using var harness = ChainHost(folder.Path);
        var projectId = await SeedProjectAsync(harness, folder.Path, executable: true);
        Chain(harness).FailTheDeterministicGateOnAMissingRunner();
        var runId = await harness.StartRunAsync(ImplementOnly, Input(projectId));
        var supervisor = Substitute.For<IDevelopmentAttemptExecutionSupervisor>();
        supervisor.StartValidation(Arg.Any<Guid>()).Returns(true);
        await harness.AdvanceUntilAsync(runId,
            async () => (await harness.ReadNodeRunAsync(runId, "impl")).DevelopmentTaskId is { } driven
                        && (await ReadTaskAsync(harness, driven)).Status == DevelopmentTaskStatus.Blocked,
            "the missing runner never stood the task down");
        var taskId = TaskOf(await harness.ReadNodeRunAsync(runId, "impl"));
        AssertEx.Equal(GraphWorkflowRunStatus.Running, (await harness.ReadRunAsync(runId)).Status, "the precondition: the run that stood it down is still live.");

        var refusal = await AssertEx.ThrowsAsync<DevelopmentInvalidTransitionException>(() => ContinueFromThePageAsync(harness, supervisor, projectId, taskId));
        AssertEx.Contains(refusal.Message, $"Graph workflow run '{runId:D}'");
        AssertEx.Equal(DevelopmentTaskStatus.Blocked, (await ReadTaskAsync(harness, taskId)).Status, "a refused continue moves nothing.");

        _ = await harness.AdvanceUntilQuiescentAsync(runId, maxTicks: 40);
        AssertEx.Equal(GraphWorkflowRunStatus.Failed, (await harness.ReadRunAsync(runId)).Status);

        var continued = await ContinueFromThePageAsync(harness, supervisor, projectId, taskId);

        AssertEx.Equal("Validation", continued.Action, "the same succeeded coder attempt is validated again, no coder round spent.");
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, (await ReadTaskAsync(harness, taskId)).Status);
        _ = supervisor.Received(1).StartValidation(taskId);
        _ = supervisor.DidNotReceive().StartAttempt(Arg.Any<Guid>(), Arg.Any<DevelopmentAttemptRole>());
    }

    /// <summary>
    ///     The lane's own fail-closed check: an <c>Any</c>-join Pause fed by start AND implement is admitted through start once
    ///     implement fails, and an Approve there reaches the apply, which refuses because no implemented task awaits it.
    /// </summary>
    [Test]
    public async Task Apply_PastAnAnyJoinPauseWhoseImplementFailed_IsRefusedAndAppliesNothing()
    {
        using var folder = new TempDirectory("xe-devtask-lane");
        await using var harness = ChainHost(folder.Path);
        var projectId = await SeedProjectAsync(harness, folder.Path);
        Chain(harness).FailNextAttempts(1);
        var runId = await harness.StartRunAsync(AnyJoinReviewApply, Input(projectId));
        _ = await harness.AdvanceUntilQuiescentAsync(runId, maxTicks: 40);
        var implemented = await harness.ReadNodeRunAsync(runId, "impl");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, implemented.Status, "the precondition: implement failed.");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.WaitingForApproval, (await harness.ReadNodeRunAsync(runId, "review")).Status, "and the review was admitted through start anyway.");

        _ = await harness.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve);
        _ = await harness.AdvanceUntilQuiescentAsync(runId, maxTicks: 40);

        var applied = await harness.ReadNodeRunAsync(runId, "apply");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, applied.Status, applied.OutputJson);
        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, applied.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(applied.Error), "Node 'apply' applies the task of 'impl', which has no Development task awaiting apply in this run.");
        AssertEx.Empty(Chain(harness).Offered, "nothing was offered to the apply gate.");
        AssertEx.Empty(Chain(harness).Callers, "and nothing asked it.");
        AssertEx.False((await ListTasksAsync(harness, projectId)).Any(static task => task.Status == DevelopmentTaskStatus.Completed), "no task was applied.");
    }

    /// <summary>
    ///     A cancel landing between the task create and its pin: the pin fails, so the lane cancels the task it just created rather
    ///     than leaving a Planned task no run owns on the Development page.
    /// </summary>
    /// <remarks>In-process the cancel wins the run's concurrency token (the tick's context holds the run it read), so both that lost race and the store's own decline are covered.</remarks>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ACancelBetweenCreateAndPin_CancelsTheJustCreatedTask(bool storeDeclines)
    {
        using var folder = new TempDirectory("xe-devtask-lane");
        var beforePin = new StrongBox<Func<Task<GraphWorkflowConditionalWriteResult?>>?>();
        await using var harness = ChainHost(folder.Path, wrapStore: inner => BeforePinGraphWorkflowStore.Wrap(inner, beforePin));
        var projectId = await SeedProjectAsync(harness, folder.Path);
        var seeded = (await ListTasksAsync(harness, projectId)).Single();
        var runId = await harness.StartRunAsync(ImplementOnly, Input(projectId));
        beforePin.Value = async () =>
        {
            await harness.CancelAsync(runId);
            return storeDeclines
                ? new GraphWorkflowConditionalWriteResult.Declined
                {
                    Reason = GraphWorkflowWriteDecline.RunNotActive
                }
                : null;
        };

        _ = await harness.AdvanceUntilQuiescentAsync(runId, maxTicks: 40);

        AssertEx.Null(beforePin.Value, "the precondition: the cancel landed between the create and the pin.");
        AssertEx.Null((await harness.ReadNodeRunAsync(runId, "impl")).DevelopmentTaskId, "the pin was declined.");
        var created = (await ListTasksAsync(harness, projectId)).Single(task => task.Id != seeded.Id);
        AssertEx.Equal(DevelopmentTaskStatus.Cancelled, created.Status, "the task no run owns is cancelled, not left Planned.");
        AssertEx.Equal(DevelopmentTaskStatus.Planned, (await ReadTaskAsync(harness, seeded.Id)).Status, "the project's own task is untouched.");
        AssertEx.Empty(Chain(harness).Actions, "nothing was started on it.");
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId)).Status);
    }

    /// <summary>
    ///     The whole shape on the scripted chain: implement to <c>AwaitingApply</c>, the run parks on the Pause, an Approve
    ///     admits the apply, and the apply names THIS run, engine included, so the task ends applied and the run completes.
    /// </summary>
    [Test]
    public async Task ImplementPauseApprovedApply_TakesTheTaskFromAwaitingApplyToApplied()
    {
        using var folder = new TempDirectory("xe-devtask-lane");
        await using var harness = ChainHost(folder.Path);
        var projectId = await SeedProjectAsync(harness, folder.Path);
        var runId = await harness.StartRunAsync(ImplementReviewApply, Input(projectId));

        _ = await harness.AdvanceUntilQuiescentAsync(runId, maxTicks: 40);
        var taskId = TaskOf(await harness.ReadNodeRunAsync(runId, "impl"));
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, (await ReadTaskAsync(harness, taskId)).Status);
        AssertEx.Equal(GraphWorkflowRunStatus.WaitingForApproval, (await harness.ReadRunAsync(runId)).Status);
        AssertEx.Empty(Chain(harness).Offered, "nothing is applied before the approval.");

        _ = await harness.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve);
        _ = await harness.AdvanceUntilQuiescentAsync(runId, maxTicks: 40);

        var applied = await harness.ReadNodeRunAsync(runId, "apply");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, applied.Status, applied.Error ?? applied.OutputJson);
        AssertEx.Contains(AssertEx.NotNull(applied.OutputJson), "\"alreadyApplied\":false");
        AssertEx.Equal(DevelopmentTaskStatus.Completed, (await ReadTaskAsync(harness, taskId)).Status);
        AssertEx.Equal(new DevelopmentWorkflowRunReference
            {
                Engine = DevelopmentWorkflowEngine.GraphWorkflow,
                RunId = runId
            },
            Chain(harness).Callers.Single(),
            "the apply names its own run and engine, which is what gets it past the ownership guard.");
        AssertEx.Equal(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId)).Status);
    }

    /// <summary>
    ///     The gate declining the patch, as it does when the repository is off the approved base, fails the apply with the
    ///     gate's reason, applies nothing and fails the run: asking again would get the same answer.
    /// </summary>
    [Test]
    public async Task Apply_TheGateDeclines_FailsTheNodeWithItsReasonAndAppliesNothing()
    {
        using var folder = new TempDirectory("xe-devtask-lane");
        await using var harness = ChainHost(folder.Path);
        var projectId = await SeedProjectAsync(harness, folder.Path);
        Chain(harness).AllowApplies(0);
        var runId = await harness.StartRunAsync(ImplementReviewApply, Input(projectId));
        _ = await harness.AdvanceUntilQuiescentAsync(runId, maxTicks: 40);
        var taskId = TaskOf(await harness.ReadNodeRunAsync(runId, "impl"));

        _ = await harness.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve);
        _ = await harness.AdvanceUntilQuiescentAsync(runId, maxTicks: 40);

        var applied = await harness.ReadNodeRunAsync(runId, "apply");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, applied.Status, applied.OutputJson);
        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, applied.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(applied.Error), "apply gate declined the patch");
        AssertEx.Equal(expected: 1, Chain(harness).Offered.Count, "the gate was asked once.");
        AssertEx.False((await ReadTaskAsync(harness, taskId)).Status == DevelopmentTaskStatus.Completed, "nothing was applied.");
        AssertEx.Equal(GraphWorkflowRunStatus.Failed, (await harness.ReadRunAsync(runId)).Status);
    }

    /// <summary>A task an operator already applied is the same answer arriving earlier: the apply succeeds without asking again.</summary>
    [Test]
    public async Task Apply_OnAnAlreadyAppliedTask_SucceedsWithoutAskingTheGate()
    {
        using var folder = new TempDirectory("xe-devtask-lane");
        await using var harness = ChainHost(folder.Path);
        var projectId = await SeedProjectAsync(harness, folder.Path);
        var runId = await harness.StartRunAsync(ImplementReviewApply, Input(projectId));
        _ = await harness.AdvanceUntilQuiescentAsync(runId, maxTicks: 40);
        var taskId = TaskOf(await harness.ReadNodeRunAsync(runId, "impl"));
        _ = await Chain(harness).ApplyAsync(projectId, taskId, Guid.NewGuid(), onBehalfOfWorkflowRun: null);
        AssertEx.Equal(DevelopmentTaskStatus.Completed, (await ReadTaskAsync(harness, taskId)).Status, "the precondition: applied out of band.");

        _ = await harness.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve);
        _ = await harness.AdvanceUntilQuiescentAsync(runId, maxTicks: 40);

        var applied = await harness.ReadNodeRunAsync(runId, "apply");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, applied.Status, applied.Error ?? applied.OutputJson);
        AssertEx.Contains(AssertEx.NotNull(applied.OutputJson), "\"alreadyApplied\":true");
        AssertEx.Equal(expected: 1, Chain(harness).Offered.Count, "the lane never offered the patch a second time.");
    }

    /// <summary>
    ///     The REAL guard: while the run is live an operator apply is refused naming it, and the run's own apply lane gets
    ///     past the guard (it then fails on the staged task's missing evidence, which is not the guard's sentence).
    /// </summary>
    [Test]
    public async Task Apply_OnBehalfOfItsOwnRun_PassesTheRealOwnershipGuard()
    {
        using var folder = new TempDirectory("xe-devtask-lane");
        await using var harness = RealServiceHost(folder.Path);
        var projectId = await SeedProjectAsync(harness, folder.Path);
        var taskId = (await ListTasksAsync(harness, projectId)).Single().Id;
        var runId = await StageImplementOnTaskAsync(harness, projectId, taskId);
        await ApproveReviewAsync(harness, runId, taskId);

        var operatorRefusal = await ApplyFailureAsync(harness, projectId, taskId, onBehalfOf: null);
        AssertEx.Contains(operatorRefusal, "has not ended", message: "the precondition: this run is the task's live owner.");
        AssertEx.Contains(operatorRefusal, runId.ToString("D"));

        _ = await harness.AdvanceUntilQuiescentAsync(runId, maxTicks: 40);

        var applied = await harness.ReadNodeRunAsync(runId, "apply");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, applied.Status, "a staged task carries no evidence, so the real apply cannot land.");
        var error = AssertEx.NotNull(applied.Error);
        AssertEx.False(error.Contains("has not ended", StringComparison.Ordinal), $"the run's own lane got past its own guard: {error}");
        AssertEx.False(error.Contains("which has no Development task awaiting apply", StringComparison.Ordinal), $"and the lane really asked: {error}");
    }

    /// <summary>Two runs drive one task: the second run's apply is refused by the real guard naming the first, which is still live.</summary>
    [Test]
    public async Task Apply_WhileAnotherLiveRunOwnsTheTask_IsRefused()
    {
        using var folder = new TempDirectory("xe-devtask-lane");
        await using var harness = RealServiceHost(folder.Path);
        var projectId = await SeedProjectAsync(harness, folder.Path);
        var taskId = (await ListTasksAsync(harness, projectId)).Single().Id;
        var owner = await StageImplementOnTaskAsync(harness, projectId, taskId);
        var runId = await StageImplementOnTaskAsync(harness, projectId, taskId);
        await ApproveReviewAsync(harness, runId, taskId);

        _ = await harness.AdvanceUntilQuiescentAsync(runId, maxTicks: 40);

        var applied = await harness.ReadNodeRunAsync(runId, "apply");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, applied.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, applied.FailureClass, "a guard refusal is not retried.");
        AssertEx.Contains(AssertEx.NotNull(applied.Error), $"Graph workflow run '{owner:D}'");
        AssertEx.Contains(applied.Error, "has not ended");
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, (await ReadTaskAsync(harness, taskId)).Status, "nothing was applied.");
    }

    /// <summary>Cancelling the run stops the Dev Mode attempt the node is waiting on and ends the node; the task itself survives.</summary>
    [Test]
    public async Task CancellingTheRun_CancelsTheAttemptItWaitsOnAndEndsTheNode()
    {
        using var folder = new TempDirectory("xe-devtask-lane");
        await using var harness = ChainHost(folder.Path);
        var projectId = await SeedProjectAsync(harness, folder.Path);
        Chain(harness).HoldNextAttempt();
        var runId = await harness.StartRunAsync(ImplementOnly, Input(projectId));
        await harness.AdvanceUntilAsync(runId,
            async () => (await harness.ReadNodeRunAsync(runId, "impl")).DevelopmentTaskId is { } driven
                        && (await ListAttemptsAsync(harness, driven)).Any(static attempt => attempt.Status == DevelopmentAttemptStatus.Running),
            "the node never had a coder attempt running");
        AssertEx.Equal(expected: 0, await harness.AdvanceAsync(runId), "the lane waits on a running attempt without writing.");

        await harness.CancelAsync(runId);
        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        var cancelled = await harness.ReadNodeRunAsync(runId, "impl");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Cancelled, cancelled.Status);
        AssertEx.Equal(expected: 1, Chain(harness).CancelledAttempts.Count, "the attempt in flight was asked to stop.");
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId)).Status);
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, (await ReadTaskAsync(harness, TaskOf(cancelled))).Status, "the task survives for the Development page.");
    }

    /// <summary>
    ///     An <c>implement</c> node that declares no <c>timeoutSeconds</c> has no deadline: a coding round routinely outlasts the
    ///     node default, so the row keeps waiting on its attempt long past it, and nothing is cancelled.
    /// </summary>
    [Test]
    public async Task AnImplementNodeWithoutATimeout_IsNotEndedByTheNodeDefault()
    {
        using var folder = new TempDirectory("xe-devtask-lane");
        var clock = new ManualTimeProvider();
        await using var harness = ChainHost(folder.Path, clock);
        var projectId = await SeedProjectAsync(harness, folder.Path);
        Chain(harness).HoldNextAttempt();
        var runId = await harness.StartRunAsync(ImplementOnly, Input(projectId));
        await AdvanceUntilAnAttemptRunsAsync(harness, runId);

        clock.Advance(TimeSpan.FromSeconds(harness.CurrentOptions().DefaultNodeTimeoutSeconds * 10) + GraphWorkflowDeadline.Grace);

        AssertEx.Equal(expected: 0, await harness.AdvanceAsync(runId), "far past the node default, the lane is still only waiting.");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "impl")).Status);
        AssertEx.Empty(Chain(harness).CancelledAttempts, "and the coding round was left to run.");
    }

    /// <summary>
    ///     An explicit <c>timeoutSeconds</c> is answered by the lane itself, inside the dispatcher's grace: it cancels the
    ///     running Dev Mode attempt, so no coder round outlives the node, and fails the node <c>Timeout</c>-classified.
    /// </summary>
    [Test]
    public async Task AnExplicitTimeout_CancelsTheRunningAttemptAndFailsTheNode()
    {
        using var folder = new TempDirectory("xe-devtask-lane");
        var clock = new ManualTimeProvider();
        await using var harness = ChainHost(folder.Path, clock);
        var projectId = await SeedProjectAsync(harness, folder.Path);
        Chain(harness).HoldNextAttempt();
        var runId = await harness.StartRunAsync(ImplementOnly.Replace("\"key\": \"impl\", \"kind\": \"DevTask\",",
                "\"key\": \"impl\", \"kind\": \"DevTask\", \"timeoutSeconds\": 60,",
                StringComparison.Ordinal),
            Input(projectId));
        await AdvanceUntilAnAttemptRunsAsync(harness, runId);

        clock.Advance(TimeSpan.FromSeconds(61));
        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        var timedOut = await harness.ReadNodeRunAsync(runId, "impl");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, timedOut.Status, "the lane ended the row before the dispatcher's grace ran out.");
        AssertEx.Equal(GraphWorkflowFailureClass.AttemptsExhausted, timedOut.FailureClass, "a timeout on the node's only attempt.");
        AssertEx.Contains(AssertEx.NotNull(timedOut.Error), "60 seconds");
        AssertEx.Equal(expected: 1, Chain(harness).CancelledAttempts.Count, "the running coder round was cancelled, not orphaned.");
        AssertEx.Equal(GraphWorkflowRunStatus.Failed, (await harness.ReadRunAsync(runId)).Status);
    }

    /// <summary>
    ///     The node's <c>instructions</c> govern its task once per node-run attempt: recorded before Dev Mode is first asked and
    ///     cleared when the attempt ends, failed or succeeded, so a re-attempt records its own and nothing outlives the node run.
    /// </summary>
    [Test]
    public async Task Implement_RecordsItsInstructionsOncePerAttemptAndClearsThemWhenEachAttemptEnds()
    {
        using var folder = new TempDirectory("xe-devtask-lane");
        await using var harness = ChainHost(folder.Path);
        var projectId = await SeedProjectAsync(harness, folder.Path);
        Chain(harness).FailNextAttempts(1);
        var governingAtEachAsk = new List<string?>();
        Chain(harness).BeforeNextAction = async taskId => governingAtEachAsk.Add(await PolicyOutcomesAsync(harness, projectId, taskId) is [.., var latest] ? latest : null);
        var runId = await harness.StartRunAsync(ImplementOnly.Replace("\"key\": \"impl\", \"kind\": \"DevTask\",",
                "\"key\": \"impl\", \"kind\": \"DevTask\", \"maxAttempts\": 2,",
                StringComparison.Ordinal),
            Input(projectId));

        _ = await harness.AdvanceUntilQuiescentAsync(runId, maxTicks: 40);

        var implemented = await harness.ReadNodeRunAsync(runId, "impl");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, implemented.Status, implemented.Error ?? implemented.OutputJson);
        AssertEx.Equal(expected: 2, implemented.Attempt, "the precondition: the first attempt failed and the second succeeded.");
        AssertEx.Equal("Applied, Cleared, Applied, Cleared", string.Join(", ", await PolicyOutcomesAsync(harness, projectId, TaskOf(implemented))),
            "each attempt records the instructions once and clears them when it ends.");
        AssertEx.True(governingAtEachAsk.Count > 0 && governingAtEachAsk.All(static outcome => outcome == "Applied"),
            $"the instructions governed every ask Dev Mode was given: {string.Join(", ", governingAtEachAsk)}");
    }

    /// <summary>
    ///     The ends that stand a running attempt down instead of reading the task, a run cancel and an explicit timeout, clear the
    ///     instructions too: a task the Development page continues is not governed by a node run that has ended.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AStoodDownImplement_ClearsTheInstructionsItRecorded(bool cancelTheRun)
    {
        using var folder = new TempDirectory("xe-devtask-lane");
        var clock = new ManualTimeProvider();
        await using var harness = ChainHost(folder.Path, clock);
        var projectId = await SeedProjectAsync(harness, folder.Path);
        Chain(harness).HoldNextAttempt();
        var runId = await harness.StartRunAsync(ImplementOnly.Replace("\"key\": \"impl\", \"kind\": \"DevTask\",",
                "\"key\": \"impl\", \"kind\": \"DevTask\", \"timeoutSeconds\": 60,",
                StringComparison.Ordinal),
            Input(projectId));
        await AdvanceUntilAnAttemptRunsAsync(harness, runId);
        var taskId = TaskOf(await harness.ReadNodeRunAsync(runId, "impl"));
        AssertEx.Equal("Applied", string.Join(", ", await PolicyOutcomesAsync(harness, projectId, taskId)), "the precondition: the running attempt is governed.");

        if (cancelTheRun)
        {
            await harness.CancelAsync(runId);
        }
        else
        {
            clock.Advance(TimeSpan.FromSeconds(61));
        }

        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        AssertEx.Equal(cancelTheRun ? GraphWorkflowNodeRunStatus.Cancelled : GraphWorkflowNodeRunStatus.Failed, (await harness.ReadNodeRunAsync(runId, "impl")).Status);
        AssertEx.Equal("Applied, Cleared", string.Join(", ", await PolicyOutcomesAsync(harness, projectId, taskId)), "the stand-down cleared what the attempt recorded.");
    }

    /// <summary>The live switch off: the node fails <c>ValidationFailed</c> with a sentence and starts and creates nothing.</summary>
    [Test]
    public async Task DevelopmentModeSwitchedOffLive_FailsTheNodeCleanly()
    {
        using var folder = new TempDirectory("xe-devtask-lane");
        await using var harness = ChainHost(folder.Path);
        var projectId = await SeedProjectAsync(harness, folder.Path);
        var runId = await harness.StartRunAsync(ImplementOnly, Input(projectId));
        _ = await harness.Services.GetRequiredService<INodeSettingsStore>().UpdateAsync(static settings => settings with
        {
            DevelopmentEnabled = false
        });

        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        var failed = await harness.ReadNodeRunAsync(runId, "impl");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, failed.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, failed.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(failed.Error), "Development Mode is switched off");
        AssertEx.Empty(Chain(harness).Actions, "nothing was asked of Dev Mode.");
        AssertEx.Equal(expected: 1, (await ListTasksAsync(harness, projectId)).Count, "and no task was created.");
    }

    /// <summary>Off at startup registers no Development services: a run that got in anyway fails the node, never a DI error.</summary>
    [Test]
    public async Task DevelopmentModeOffAtStartup_FailsTheNodeRatherThanTheContainer()
    {
        await using var host = GraphWorkflowDevTaskHostFixture.DevelopmentModeOff();
        await using (var probe = host.Services.CreateAsyncScope())
        {
            AssertEx.Null(probe.ServiceProvider.GetService<IDevelopmentManagementService>(), "the precondition: the module registered nothing.");
        }

        var runId = await StartThroughTheStoreAsync(host.Services, ImplementOnly, Input(Guid.NewGuid()));
        var dispatcher = host.Services.GetRequiredService<GraphWorkflowDispatcher>();
        for (var tick = 0; tick < 20; tick++)
        {
            if (await dispatcher.AdvanceOnceAsync(runId, CancellationToken.None) == 0)
            {
                break;
            }
        }

        await using var scope = host.Services.CreateAsyncScope();
        var failed = (await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().ListNodeRunsAsync(runId)).Single(static nodeRun => nodeRun.NodeKey == "impl");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, failed.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, failed.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(failed.Error), "Development Mode is switched off");
    }

    private static GraphWorkflowHarness ChainHost(string repositoryRoot, TimeProvider? clock = null, Func<IGraphWorkflowStore, IGraphWorkflowStore>? wrapStore = null) =>
        GraphWorkflowHarness.PrivateHost(services =>
        {
            services.RemoveAll<IDevelopmentManagementService>();
            services.AddSingleton<IDevelopmentManagementService>(static provider => new FakeDevelopmentTaskChain(provider.GetRequiredService<IServiceScopeFactory>()));
            AcceptEveryProject(services, repositoryRoot);
            if (clock is not null)
            {
                services.AddSingleton(clock);
            }

            if (wrapStore is not null)
            {
                // Around the container's own registration, so the publishing decorator stays underneath.
                var registered = services.Single(static descriptor => descriptor.ServiceType == typeof(IGraphWorkflowStore));
                var build = registered.ImplementationFactory ?? throw new AssertionException("The graph workflow store is expected to be registered through a factory.");
                _ = services.Remove(registered);
                services.AddScoped(provider => wrapStore((IGraphWorkflowStore)build(provider)));
            }
        });

    private static GraphWorkflowHarness RealServiceHost(string repositoryRoot) =>
        GraphWorkflowHarness.PrivateHost(services => AcceptEveryProject(services, repositoryRoot));

    /// <summary>The start binding resolves every project to the test's own folder, so no run is refused for a repository it never touches.</summary>
    internal static void AcceptEveryProject(IServiceCollection services, string repositoryRoot)
    {
        var bindings = Substitute.For<IDevelopmentRepositoryBindingService>();
        bindings.ResolveProjectAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(call => new DevelopmentRepositoryBinding
                {
                    ProjectId = call.ArgAt<Guid>(0),
                    SelectedFolderId = Guid.NewGuid(),
                    Alias = "repository",
                    RepositoryRoot = repositoryRoot,
                    RepositoryIdentityHash = "repository-identity-hash"
                });
        services.RemoveAll<IDevelopmentRepositoryBindingService>();
        services.AddSingleton(bindings);
    }

    private static Task AdvanceUntilAnAttemptRunsAsync(GraphWorkflowHarness harness, Guid runId) =>
        harness.AdvanceUntilAsync(runId,
            async () => (await harness.ReadNodeRunAsync(runId, "impl")).DevelopmentTaskId is { } driven
                        && (await ListAttemptsAsync(harness, driven)).Any(static attempt => attempt.Status == DevelopmentAttemptStatus.Running),
            "the node never had a coder attempt running");

    private static Guid TaskOf(GraphWorkflowNodeRunSnapshot nodeRun) =>
        nodeRun.DevelopmentTaskId ?? throw new AssertionException($"Node run '{nodeRun.NodeKey}' names no Development task.");

    private static FakeDevelopmentTaskChain Chain(GraphWorkflowHarness harness) =>
        (FakeDevelopmentTaskChain)harness.Services.GetRequiredService<IDevelopmentManagementService>();

    private static string Input(Guid projectId) =>
        $$"""{ "developmentProjectId": "{{projectId}}" }""";

    /// <summary>The Development page's next action on the REAL management service, beside a container whose own is the scripted chain.</summary>
    private static async Task<DevelopmentNextActionResult> ContinueFromThePageAsync(GraphWorkflowHarness harness,
        IDevelopmentAttemptExecutionSupervisor supervisor,
        Guid projectId,
        Guid taskId)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var page = ActivatorUtilities.CreateInstance<DevelopmentManagementService>(scope.ServiceProvider, supervisor);
        return await page.StartNextActionAsync(projectId, taskId, Guid.NewGuid());
    }

    /// <summary>
    ///     A real Development project, with the one task every project is created with; <paramref name="executable" /> adds the
    ///     trust acknowledgement and command profile the real service checks before any next action.
    /// </summary>
    internal static async Task<Guid> SeedProjectAsync(GraphWorkflowHarness harness, string hostPath, bool executable = false)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var folder = await scope.ServiceProvider.GetRequiredService<ISelectedFolderResolver>()
                                .RegisterAsync(new SelectedFolderRegistration
                                {
                                    Alias = $"lane-{Guid.NewGuid():N}"[..20],
                                    HostPath = hostPath
                                });
        var projectId = Guid.NewGuid();
        _ = await scope.ServiceProvider.GetRequiredService<IDevelopmentStore>()
                       .CreateProjectAsync(new DevelopmentCreateProjectCommand
                       {
                           ProjectId = projectId,
                           TaskId = Guid.NewGuid(),
                           OperationId = Guid.NewGuid(),
                           Objective = "Keep the product working.",
                           SelectedFolderId = Guid.Parse(folder.Id),
                           RepositoryIdentityHash = "repository-identity-hash",
                           BaseBranch = "main",
                           Title = "Add the feature",
                           Requirements = "It has to do the thing.",
                           AcceptanceCriteriaJson = "[\"it does the thing\"]",
                           CoderModelId = executable ? "coder-model" : null,
                           ReviewerModelId = executable ? "reviewer-model" : null,
                           TrustedRepositoryAcknowledged = executable,
                           TrustedRepositoryPolicyVersion = executable ? DevelopmentTrustPolicy.CurrentVersion : null,
                           TrustedRepositoryAcknowledgedAtUtc = executable ? scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeMilliseconds() : null,
                           CommandProfileJson = executable
                               ? Encoding.UTF8.GetString(DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.PythonPytest, buildTarget: null).ToCanonicalUtf8())
                               : null
                       });
        return projectId;
    }

    /// <summary>
    ///     A run whose <c>impl</c> row is opened and pinned to <paramref name="taskId" /> by hand, the task walked to
    ///     <c>AwaitingApply</c> out of band: what the lane leaves behind, without a real coder chain the real service would start.
    /// </summary>
    private static async Task<Guid> StageImplementOnTaskAsync(GraphWorkflowHarness harness, Guid projectId, Guid taskId)
    {
        var runId = await harness.StartRunAsync(ImplementReviewApply, Input(projectId));
        _ = await harness.AdvanceAsync(runId);
        AssertEx.Equal(GraphWorkflowRunStatus.Running, (await harness.ReadRunAsync(runId)).Status);
        await harness.TransitionNodeRunAsync(runId, "impl", GraphWorkflowNodeRunStatus.Running);
        var implement = await harness.ReadNodeRunAsync(runId, "impl");
        await using var scope = harness.Services.CreateAsyncScope();
        AssertEx.True(await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().SetNodeRunDevelopmentTaskAsync(runId, implement.Id, taskId)
            is GraphWorkflowConditionalWriteResult.Written);
        var development = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        if ((await development.GetTaskAsync(taskId)).Status != DevelopmentTaskStatus.Planned)
        {
            // A second run staged on the same task finds it walked already.
            return runId;
        }

        foreach (var target in new[]
                 {
                     DevelopmentTaskStatus.Ready,
                     DevelopmentTaskStatus.InProgress,
                     DevelopmentTaskStatus.Validation,
                     DevelopmentTaskStatus.InReview,
                     DevelopmentTaskStatus.AwaitingApply
                 })
        {
            var task = await development.GetTaskAsync(taskId);
            _ = await development.TransitionTaskAsync(new DevelopmentTransitionTaskCommand
            {
                TaskId = taskId,
                OperationId = Guid.NewGuid(),
                TargetStatus = target,
                ExpectedTaskVersion = task.Version,
                ApprovedSubjectHash = target == DevelopmentTaskStatus.AwaitingApply ? "subject" : null
            });
        }

        return runId;
    }

    /// <summary>Ticks the staged run until its review parks (the lane settles <c>impl</c> off the task on the way), then approves it.</summary>
    private static async Task ApproveReviewAsync(GraphWorkflowHarness harness, Guid runId, Guid taskId)
    {
        await harness.AdvanceUntilAsync(runId,
            async () => (await harness.ReadNodeRunAsync(runId, "review")).Status == GraphWorkflowNodeRunStatus.WaitingForApproval,
            "the review never parked");
        AssertEx.Equal(taskId, (await harness.ReadNodeRunAsync(runId, "impl")).DevelopmentTaskId);
        _ = await harness.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve);
    }

    /// <summary>The message the real service refused an apply with, whatever refused it.</summary>
    private static async Task<string> ApplyFailureAsync(GraphWorkflowHarness harness, Guid projectId, Guid taskId, DevelopmentWorkflowRunReference? onBehalfOf)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var management = scope.ServiceProvider.GetRequiredService<IDevelopmentManagementService>();
        return (await AssertEx.ThrowsAsync<Exception>(() => management.ApplyAsync(projectId, taskId, Guid.NewGuid(), onBehalfOf),
            "a staged task carries no evidence, so the apply cannot succeed.")).Message;
    }

    /// <summary>A run written straight through the store, for a host whose run service refuses to start a DevTask graph.</summary>
    private static async Task<Guid> StartThroughTheStoreAsync(IServiceProvider services, string graphJson, string inputJson)
    {
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>();
        var graph = GraphWorkflowGraph.Parse(graphJson);
        using var document = JsonDocument.Parse(graphJson);
        var definition = await store.CreateDefinitionAsync(new CreateGraphWorkflowDefinitionCommand
        {
            DefinitionId = Guid.NewGuid(),
            Name = $"Lane {Guid.NewGuid():N}",
            GraphJson = graphJson,
            NodeCount = document.RootElement.GetProperty("nodes").GetArrayLength(),
            Kind = GraphWorkflowDefinitionKind.Standard
        });
        var run = await store.StartRunAsync(new StartGraphWorkflowRunCommand
        {
            RunId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            DefinitionId = definition.Id,
            DefinitionVersion = definition.Version,
            GraphHash = definition.GraphHash,
            GraphJson = graphJson,
            InputJson = inputJson,
            TriggerSource = GraphWorkflowTriggerSource.Manual,
            NodeRuns =
            [
                .. graph.Nodes.Values.Select(static node => new GraphWorkflowNodeRunSeed
                {
                    NodeRunId = Guid.NewGuid(),
                    NodeKey = node.NodeKey,
                    Kind = node.Kind
                })
            ]
        });
        return run.Id;
    }

    internal static async Task<IReadOnlyList<DevelopmentTaskSnapshot>> ListTasksAsync(GraphWorkflowHarness harness, Guid projectId)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDevelopmentStore>().ListTasksAsync(projectId);
    }

    /// <summary>The task's instruction rows in order, as the outcome each was written with: <c>Applied</c> or <c>Cleared</c>.</summary>
    private static async Task<IReadOnlyList<string?>> PolicyOutcomesAsync(GraphWorkflowHarness harness, Guid projectId, Guid taskId)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        return
        [
            .. (await scope.ServiceProvider.GetRequiredService<IDevelopmentStore>().ListEventsAsync(projectId))
               .Where(entry => entry.TaskId == taskId && entry.EventType == "WorkflowPolicyApplied")
               .Select(static entry => entry.Outcome)
        ];
    }

    private static async Task<IReadOnlyList<DevelopmentAttemptSnapshot>> ListAttemptsAsync(GraphWorkflowHarness harness, Guid taskId)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDevelopmentStore>().ListAttemptsAsync(taskId);
    }

    internal static async Task<DevelopmentTaskSnapshot> ReadTaskAsync(GraphWorkflowHarness harness, Guid taskId)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDevelopmentStore>().GetTaskAsync(taskId);
    }
}

/// <summary>
///     The container's store with one seam: the next <c>SetNodeRunDevelopmentTaskAsync</c> first runs the test's hook, which answers
///     <see langword="null" /> to let the real write run or a result to answer in its place.
/// </summary>
/// <remarks>Not sealed: <see cref="DispatchProxy" /> derives the proxy type at run time. Every other member forwards untouched.</remarks>
public class BeforePinGraphWorkflowStore : DispatchProxy
{
    private IGraphWorkflowStore _inner = null!;
    private StrongBox<Func<Task<GraphWorkflowConditionalWriteResult?>>?> _hook = null!;

    /// <summary>Wraps <paramref name="inner" />; <paramref name="hook" /> is shared across scopes and cleared when it runs.</summary>
    public static IGraphWorkflowStore Wrap(IGraphWorkflowStore inner, StrongBox<Func<Task<GraphWorkflowConditionalWriteResult?>>?> hook)
    {
        var proxy = Create<IGraphWorkflowStore, BeforePinGraphWorkflowStore>();
        var self = (BeforePinGraphWorkflowStore)(object)proxy;
        self._inner = inner;
        self._hook = hook;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        return targetMethod.Name == nameof(IGraphWorkflowStore.SetNodeRunDevelopmentTaskAsync) && Interlocked.Exchange(ref _hook.Value, null) is { } hook
            ? PinAfterAsync(hook, targetMethod, args)
            : targetMethod.Invoke(_inner, args);
    }

    private async Task<GraphWorkflowConditionalWriteResult> PinAfterAsync(Func<Task<GraphWorkflowConditionalWriteResult?>> hook, MethodInfo targetMethod, object?[]? args) =>
        await hook() ?? await (Task<GraphWorkflowConditionalWriteResult>)targetMethod.Invoke(_inner, args)!;
}
