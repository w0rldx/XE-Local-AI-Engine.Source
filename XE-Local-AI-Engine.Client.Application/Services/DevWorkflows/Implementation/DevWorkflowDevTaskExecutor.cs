namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Globalization;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;

/// <summary>
///     The implementation lane: a <c>DevTask</c> node run drives the Development task its work item's project owns,
///     through the chain that already exists — coder attempt, deterministic validation, independent review — and
///     succeeds when that task reaches <c>AwaitingApply</c>.
///     <para>
///         A CLIENT of Dev Mode, not a fork of it. The hash-locked apply gate is the concept's integration and
///         independent verification, and it is built on the task and attempt rows; re-implementing any of it here would
///         either rebuild that chain or weaken it. So this holds no execution state of its own: it asks for the next
///         action, reads what the task became, and writes that onto the node run.
///     </para>
///     <para>
///         <b>It never applies anything.</b> Reaching <c>AwaitingApply</c> IS the node's success — the patch waits
///         behind a workflow gate, so no AI-authored change reaches a real repository without a decision recorded in
///         this run's own audit trail.
///     </para>
///     <para>
///         <b>Which task it drives is decided once and then remembered.</b> A node run that already names one drives
///         that one — the pointer survives a reset, so a re-attempt re-drives the same task, which is Dev Mode's own
///         rework loop and leaves the per-round evidence where the rest of it already lives. A MATERIALIZED child gets
///         a task of its OWN in the same project, because it implements its own slice and two children sharing one task
///         would overwrite each other's work. Anything else — the ordinary undecomposed graph — drives the project's
///         existing task rather than creating one.
///     </para>
/// </summary>
internal sealed class DevWorkflowDevTaskExecutor
{
    /// <summary>
    ///     The attempt a child task's creation is keyed under. Not any real attempt number — attempts start at one —
    ///     because the task belongs to the node for the whole run and every re-attempt must find the same one.
    /// </summary>
    private const int ChildTaskAttempt = 0;

    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The attempt statuses that mean the chain is still working, so nothing here should ask it for more.</summary>
    private static readonly HashSet<DevelopmentAttemptStatus> ActiveAttemptStatuses =
    [
        DevelopmentAttemptStatus.Pending,
        DevelopmentAttemptStatus.Running
    ];

    /// <summary>
    ///     How much of the routed node's validation report a change request may carry. Generous enough for the failing
    ///     commands and a readable tail of each, small enough that a task's rework reason stays a sentence a human and a
    ///     model can both take in — and small enough that it cannot become the whole prompt.
    /// </summary>
    private const int MaxChangeRequestReason = 4096;

    /// <summary>How much of one failing command's captured output the change request quotes, from the END of it.</summary>
    private const int MaxQuotedCommandOutput = 800;

    private readonly IDevWorkflowArtifactBlobStore _blobs;
    private readonly ILogger<DevWorkflowDevTaskExecutor> _logger;
    private readonly DevWorkflowRetryPolicy _retries;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _timeProvider;

    public DevWorkflowDevTaskExecutor(IServiceProvider services,
        IDevWorkflowArtifactBlobStore blobs,
        DevWorkflowRetryPolicy retries,
        TimeProvider timeProvider,
        ILogger<DevWorkflowDevTaskExecutor> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
        _retries = retries ?? throw new ArgumentNullException(nameof(retries));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    ///     Binds the node run to the task it implements and asks the chain for its next action.
    ///     <para>
    ///         No <c>Queued</c> hop: this lane hands out no slots, and a <c>Queued</c> row would have to name a queue
    ///         reason for a queue that does not exist. What bounds the work is <c>MaxConcurrentRuns</c> — Dev Mode's own
    ///         one-active-attempt rule is per TASK, so four runs on four projects legitimately drive four attempts at
    ///         once — and each of those attempts carries the development attempt-duration budget of its own.
    ///     </para>
    /// </summary>
    public async Task<int> DispatchAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(nodeRun);

        var (development, management) = Resolve();
        if (development is null || management is null)
        {
            // Development Mode is switched off on this node, so there is no task machine, no workspace and no sandbox.
            // Nothing here can run, and no retry changes that.
            return await BlockAsync(store,
                    graph,
                    run,
                    nodeRun,
                    nodeRuns,
                    DevWorkflowFailureClasses.Configuration,
                    "This node implements a development task, and Development Mode is switched off on this node.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (nodeRun.DevelopmentProjectId is not { } projectId)
        {
            // Run start refuses a graph with implementation nodes on a work item that names no project, so this is a row
            // materialized before such a node existed rather than an ordinary miss.
            return await BlockAsync(store,
                    graph,
                    run,
                    nodeRun,
                    nodeRuns,
                    DevWorkflowFailureClasses.Configuration,
                    $"Node run '{nodeRun.NodeKey}' implements a development task but names no development project to implement it in.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        Guid taskId;
        try
        {
            if (await ResolveTaskAsync(graph, run, nodeRun, development, projectId, cancellationToken).ConfigureAwait(false) is not { } resolved)
            {
                return await BlockAsync(store,
                        graph,
                        run,
                        nodeRun,
                        nodeRuns,
                        DevWorkflowFailureClasses.Configuration,
                        "The development project this node implements carries no task to implement.",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            taskId = resolved;
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
        {
            // A brief this node cannot be given a task from, or a project that is gone. Both are decided facts, and an
            // escape here would be the worst of the failure modes available: the row is still Pending, so no deadline
            // can fire on it and the sweep would re-dispatch it every tick forever. Both messages are this engine's own
            // text about its own rows — no host path, no model output. A DevelopmentConcurrencyException deliberately
            // does NOT land here: a lost ledger race is transient, and the next sweep is the right answer to it.
            return await BlockAsync(store, graph, run, nodeRun, nodeRuns, DevWorkflowFailureClasses.Configuration, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }

        // Read from the same clock the store stamps the row with, and read BEFORE the write so it is a lower bound on
        // that stamp. The dispatch path has to carry it: the row this call goes on to judge attempts against is the one
        // composed below, and a snapshot still holding the pre-dispatch null would make that comparison vacuous.
        var startedAt = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        // The pointer is written with the status, in the same transaction: a row reading Running with no task named is
        // a row nothing can poll, and this is the only write that could leave one.
        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Running, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               DevWorkflowVersions.Any,
                               DevWorkflowNodeRunStatus.Running,
                               DevelopmentTaskId: taskId),
                           cancellationToken)
                       .ConfigureAwait(false);

        return 1 + await AdvanceTaskAsync(store,
                graph,
                run,
                nodeRun with
                {
                    Status = DevWorkflowNodeRunStatus.Running,
                    DevelopmentTaskId = taskId,
                    StartedAtUtc = startedAt
                },
                nodeRuns,
                development,
                management,
                projectId,
                taskId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     The task this node run implements: the one it already names, its own newly created one when it is a
    ///     materialized child, or the project's existing task.
    ///     <para>
    ///         The pointer comes FIRST and is never second-guessed. It survives a reset by design (a previous attempt's
    ///         task is that attempt's evidence), so re-resolving here would let a re-attempt walk away from work that
    ///         is already under way.
    ///     </para>
    ///     <para>
    ///         A materialized child implements its own slice of the project, so it gets its own task. Creating it is
    ///         keyed on the run and node key WITHOUT the attempt — the task belongs to the node for the life of the run
    ///         — so a crash between the create and the pointer write is answered by the same task on re-dispatch rather
    ///         than by a second one nothing points at.
    ///     </para>
    ///     <para>
    ///         Throws rather than answering null when a child's brief cannot describe a task: the caller turns both into
    ///         the same <c>Configuration</c> stand-down, and the two conditions need different sentences.
    ///     </para>
    /// </summary>
    private static async Task<Guid?> ResolveTaskAsync(DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IDevelopmentStore development,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        if (nodeRun.DevelopmentTaskId is { } pinned)
        {
            return pinned;
        }

        var tasks = await development.ListTasksAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (tasks.Count == 0)
        {
            return null;
        }

        if (nodeRun.MaterializedFromNodeRunId is null)
        {
            return tasks[0].Id;
        }

        // The project's first task is the operator-authored one, and it carries the standard this project's work is
        // judged against: the child inherits its acceptance criteria and review budget. What it must NOT inherit is the
        // requirements — that would hand every child the whole feature, N times over, and each of them would look like
        // a legitimately configured task while doing it.
        var brief = Brief(nodeRun.InputJson);
        var requirements = Present(brief?.Requirements)
                           ?? throw new ArgumentException($"Node run '{nodeRun.NodeKey}' is a materialized development task whose input names no 'requirements' to implement.",
                               nameof(nodeRun));
        var created = await development.CreateTaskAsync(new DevelopmentCreateTaskCommand(projectId,
                                           Guid.NewGuid(),
                                           DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, ChildTaskAttempt, "devtask-create"),
                                           Present(brief?.Title) ?? Label(graph, nodeRun.NodeKey),
                                           requirements,
                                           Present(brief?.AcceptanceCriteriaJson) ?? tasks[0].AcceptanceCriteriaJson,
                                           tasks[0].MaxReviewRounds),
                                       cancellationToken)
                                   .ConfigureAwait(false);
        return created.TaskId;
    }

    /// <summary>A brief's field, or nothing — a present-but-blank string is an absent one, not a value to pass on.</summary>
    private static string? Present(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    ///     What a materialized child was told to implement — the seam decomposition writes through.
    ///     <para>
    ///         <c>requirements</c> is MANDATORY: an input that is absent, unparseable, shaped for something else, or
    ///         blank there stands the node down for a human. The only thing in this repository that writes these rows is
    ///         decomposition, so a missing brief is a bug in the thing that materialized the child — and inheriting the
    ///         parent's requirements would hide it behind N children each implementing the entire feature.
    ///     </para>
    ///     <para>
    ///         <c>title</c> falls back to the node's own label, and <c>acceptanceCriteriaJson</c> to the project's first
    ///         task: neither says what to build, and the project's standard of done is the right one for a slice of it.
    ///     </para>
    /// </summary>
    private static DevTaskBrief? Brief(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DevTaskBrief>(inputJson, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // NotSupportedException as well as JsonException: a document whose shape the converter refuses outright
            // throws the former, and it would otherwise escape a path that has no answer for it.
            return null;
        }
    }

    private static string Label(DevWorkflowGraph graph, string nodeKey) =>
        graph.Nodes.TryGetValue(nodeKey, out var node) && !string.IsNullOrWhiteSpace(node.Label) ? node.Label : nodeKey;

    /// <summary>
    ///     Reads what the development chain has made of the task and settles the node run when it has finished with it,
    ///     answering how many transitions it wrote.
    ///     <para>
    ///         A stage boundary counts as work even though it writes no row of this module's: the chain runs one attempt
    ///         per request, so the tick that asks for the next one has moved the run on, and saying otherwise would
    ///         leave the whole implementation waiting a sweep interval per stage.
    ///     </para>
    /// </summary>
    public async Task<int> PollAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(nodeRun);

        var (development, management) = Resolve();
        if (development is null || management is null)
        {
            return await BlockAsync(store,
                    graph,
                    run,
                    nodeRun,
                    nodeRuns,
                    DevWorkflowFailureClasses.Configuration,
                    "This node implements a development task, and Development Mode is switched off on this node.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (nodeRun is not { DevelopmentProjectId: { } projectId, DevelopmentTaskId: { } taskId })
        {
            return await BlockAsync(store,
                    graph,
                    run,
                    nodeRun,
                    nodeRuns,
                    DevWorkflowFailureClasses.Internal,
                    "This node run is running without a development task, so nothing can report what it is doing.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await AdvanceTaskAsync(store, graph, run, nodeRun, nodeRuns, development, management, projectId, taskId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Asks the node run's task to stop, for whichever drain the run is in, and answers how many transitions it
    ///     wrote.
    ///     <para>
    ///         A pause lets the attempt in flight finish, for the same reason it lets a build finish and a stronger one:
    ///         a coder attempt is a model conversation with a workspace behind it, and it cannot be resumed halfway. The
    ///         row then collapses to <c>Pending</c> exactly as a paused agent node run does, and the resume re-drives
    ///         the task from wherever the chain left it — durably, because the task is a row rather than a process.
    ///     </para>
    /// </summary>
    public async Task<int> StopAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        bool cancel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(nodeRun);

        if (await StopAttemptAsync(nodeRun, cancel, cancellationToken).ConfigureAwait(false))
        {
            // Under a cancel the attempt has been asked to stop and the next tick's poll settles the row on what it
            // actually did; under a pause it is simply left to finish, and the run stays Pausing until it has.
            return cancel ? 1 : 0;
        }

        var target = cancel ? DevWorkflowNodeRunStatus.Cancelled : DevWorkflowNodeRunStatus.Pending;
        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, target, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               DevWorkflowVersions.Any,
                               target,
                               FailureClass: cancel ? DevWorkflowFailureClasses.Cancelled : null,
                               TerminalReason: cancel ? "The run was cancelled while this node run was implementing its development task." : null),
                           cancellationToken)
                       .ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    ///     Answers whether the node run's task has an attempt in flight, cancelling it on the way when the caller is
    ///     ending the node run rather than parking it.
    ///     <para>
    ///         Deliberately does not touch the node run: a cancelled attempt is still winding down, and only the next
    ///         tick's poll knows whether it stopped or finished inside the window. The one caller that DOES write a
    ///         terminal off this — the node deadline — writes its own, because the clock is the reason and the attempt's
    ///         cancellation is only the consequence.
    ///     </para>
    /// </summary>
    public async Task<bool> StopAttemptAsync(DevWorkflowNodeRunSnapshot nodeRun, bool cancel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeRun);

        var (development, management) = Resolve();
        if (development is null
            || management is null
            || nodeRun is not { DevelopmentProjectId: { } projectId, DevelopmentTaskId: { } taskId })
        {
            return false;
        }

        if ((await development.ListAttemptsAsync(taskId, cancellationToken).ConfigureAwait(false))
            .LastOrDefault(static attempt => ActiveAttemptStatuses.Contains(attempt.Status)) is not { } attempt)
        {
            return false;
        }

        if (cancel)
        {
            _ = await management.CancelAttemptAsync(projectId, taskId, attempt.Id, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    ///     The one place the task's state becomes the node run's: settle when the chain has finished with it, ask for
    ///     the next action when it is idle, and otherwise leave it alone because something is already working.
    /// </summary>
    private async Task<int> AdvanceTaskAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        IDevelopmentStore development,
        IDevelopmentManagementService management,
        Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        var task = await development.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        var attempts = await development.ListAttemptsAsync(taskId, cancellationToken).ConfigureAwait(false);
        var rework = task.Status == DevelopmentTaskStatus.AwaitingApply
            ? await UnconsumedPriorFailureAsync(development, run, nodeRun, projectId, cancellationToken).ConfigureAwait(false)
            : null;

        switch (task.Status)
        {
            case DevelopmentTaskStatus.AwaitingApply when rework is { } failingNodeKey:

                // The fix loop landed on an already-approved task. Settling it Succeeded here is what made a routed
                // re-attempt a no-op: the loop would route, re-succeed in the same tick, and spend the target's whole
                // attempt budget in seconds without ever asking for a different patch. So the node asks Dev Mode for
                // rework instead, with the routed node's own validation report as the review evidence.
                return await RequestChangesAsync(store, graph, run, nodeRun, nodeRuns, development, task, failingNodeKey, cancellationToken).ConfigureAwait(false);

            case DevelopmentTaskStatus.AwaitingApply or DevelopmentTaskStatus.Completed:

                // The node's job is DONE at AwaitingApply: an independent reviewer approved the exact subject, and
                // applying it is a later act behind a gate this run records. A task somebody has already applied is the
                // same answer arriving later.
                //
                // A re-attempt with no routed failure behind it succeeds immediately, and that is the honest answer
                // rather than a shortcut: nothing has said this implementation is wrong, and the claim the node is
                // making — this task is implemented and waiting to be applied — is still true.
                return await SettleAsync(store,
                        run,
                        nodeRun,
                        nodeRuns,
                        DevWorkflowNodeRunStatus.Succeeded,
                        failureClass: null,
                        terminalReason: null,
                        Output(nodeRun, task, taskId, failureClass: null),
                        cancellationToken)
                    .ConfigureAwait(false);

            case DevelopmentTaskStatus.Cancelled:
                return await SettleAsync(store,
                        run,
                        nodeRun,
                        nodeRuns,
                        DevWorkflowNodeRunStatus.Cancelled,
                        DevWorkflowFailureClasses.Cancelled,
                        "The development task this node run was implementing was cancelled.",
                        Output(nodeRun, task, taskId, DevWorkflowFailureClasses.Cancelled),
                        cancellationToken)
                    .ConfigureAwait(false);

            case DevelopmentTaskStatus.Blocked:

                // The chain gave up on its own terms — its review rounds ran out, or an operator stood it down. Another
                // node-run attempt would re-drive a task that is not going anywhere, so this is the class that goes
                // straight to a human.
                return await _retries.SettleFailureAsync(store,
                        graph,
                        run,
                        nodeRun,
                        nodeRuns,
                        new DevWorkflowFailure(DevWorkflowFailureClasses.BudgetExhausted,
                            task.BlockedReason ?? "The development task this node run was implementing was blocked.",
                            Output(nodeRun, task, taskId, DevWorkflowFailureClasses.BudgetExhausted)),
                        cancellationToken)
                    .ConfigureAwait(false);

            default:
                break;
        }

        if (attempts.Any(static attempt => ActiveAttemptStatuses.Contains(attempt.Status)))
        {
            // The chain is working — and this counts every attempt, not only the ones this node-run attempt started,
            // because Dev Mode's own rule is one active attempt per task and asking for another would simply be refused.
            // It drives its own attempt to completion without being ticked, so there is nothing to do but wait.
            return 0;
        }

        // Only the attempts THIS node-run attempt is answerable for. A failed attempt from a previous round is still on
        // the task — it is the evidence of that round — and reading it as this round's answer would settle every
        // re-attempt off the failure that caused it, spending the node's whole budget without ever asking the chain to
        // try again. Both instants come from the same clock and the row's is taken before anything is started, so the
        // comparison is a fact about ordering rather than a guess.
        if (attempts.LastOrDefault(attempt => attempt.StartedAtUtc >= nodeRun.StartedAtUtc) is { Status: not DevelopmentAttemptStatus.Succeeded } landed)
        {
            // The attempt failed, was interrupted by a restart, or was cancelled. Where that leads — another attempt at
            // this node, the node that produced what it was implementing, or a human — is the retry policy's answer,
            // exactly as it is for a work session that failed.
            return landed.Status == DevelopmentAttemptStatus.Cancelled
                ? await SettleAsync(store,
                        run,
                        nodeRun,
                        nodeRuns,
                        DevWorkflowNodeRunStatus.Cancelled,
                        DevWorkflowFailureClasses.Cancelled,
                        "The development attempt this node run was driving was cancelled.",
                        Output(nodeRun, task, taskId, DevWorkflowFailureClasses.Cancelled),
                        cancellationToken)
                    .ConfigureAwait(false)
                : await _retries.SettleFailureAsync(store,
                        graph,
                        run,
                        nodeRun,
                        nodeRuns,
                        Failure(landed, nodeRun, task, taskId),
                        cancellationToken)
                    .ConfigureAwait(false);
        }

        if (run.Status is DevWorkflowRunStatus.Pausing or DevWorkflowRunStatus.Cancelling)
        {
            // Never while the run is draining: asking for the next action would start work the operator has just asked
            // the run to stop doing, and the drain is about to park or end this row anyway.
            return 0;
        }

        return await StartNextActionAsync(store, graph, run, nodeRun, nodeRuns, development, management, projectId, taskId, task, attempts.Count, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Asks the chain for the next thing to do, and answers 1 because a stage boundary is progress even though the
    ///     row it moves belongs to Dev Mode.
    ///     <para>
    ///         The operation id is derived from what this tick OBSERVED — the task's status and how many attempts it has
    ///         — so a tick replayed after a crash asks for the same action rather than a second one, and a tick that
    ///         genuinely finds a new state asks for the next.
    ///     </para>
    /// </summary>
    private async Task<int> StartNextActionAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        IDevelopmentStore development,
        IDevelopmentManagementService management,
        Guid projectId,
        Guid taskId,
        DevelopmentTaskSnapshot task,
        int attemptCount,
        CancellationToken cancellationToken)
    {
        var operationId = DevWorkflowOperationId.For(run.Id,
            nodeRun.NodeKey,
            nodeRun.Attempt,
            string.Create(CultureInfo.InvariantCulture, $"devtask-{task.Status}-{task.CurrentReviewRound}-{attemptCount}"));
        try
        {
            _ = await management.StartNextActionAsync(projectId, taskId, operationId, cancellationToken).ConfigureAwait(false);
            return 1;
        }
        catch (DevelopmentInvalidTransitionException exception)
        {
            // Either the task has no next action, or something else started one between this tick's read and its ask —
            // the Development views can drive the same task. Re-reading tells the two apart without matching on a
            // message: if an attempt is running now, the run is not stuck, it is simply not this tick's to advance.
            if ((await development.ListAttemptsAsync(taskId, cancellationToken).ConfigureAwait(false))
                .Any(static attempt => ActiveAttemptStatuses.Contains(attempt.Status)))
            {
                return 0;
            }

            // An attempt row is not the only way Dev Mode is busy. Deterministic validation is a phase its own
            // supervisor drives with NO attempt row at all — it runs the project's command profile and then moves the
            // task on to InReview or ChangesRequested — so a tick landing inside that window is told there is no next
            // action, which is true and is not a fault. It stood tasks down 24 ms after validation started, with a
            // SUCCEEDED coder attempt on them and Dev Mode calmly finishing.
            //
            // The VERSION is what makes that general rather than a patch for one status. Naming Validation alone loses
            // the same race one hop later: the supervisor can finish and move the task to InReview between the ask and
            // this read, and a task that MOVED since the snapshot this tick opened with is working, whatever it moved
            // to. Only a task sitting exactly where this tick found it, with no attempt and no next action, is
            // genuinely stuck — and that is the one this blocks.
            var current = await development.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (current.Status == DevelopmentTaskStatus.Validation || current.Version != task.Version)
            {
                return 0;
            }

            return await BlockAsync(store, graph, run, nodeRun, nodeRuns, DevWorkflowFailureClasses.Configuration, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DevelopmentConcurrencyException)
        {
            // The work is already scheduled. Nothing is wrong and nothing is owed; the next tick reads what it became.
            return 0;
        }
        catch (DevelopmentWorkspaceSecurityException exception)
        {
            // A trust acknowledgement that has expired, or a model that would send this repository's contents somewhere
            // this project refuses to. Running it again produces the same refusal.
            return await BlockAsync(store, graph, run, nodeRun, nodeRuns, DevWorkflowFailureClasses.Policy, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (KeyNotFoundException exception)
        {
            return await BlockAsync(store, graph, run, nodeRun, nodeRuns, DevWorkflowFailureClasses.Configuration, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The message is NOT surfaced: an unexpected exception's text is the one string on this path nothing has
            // sanitized, and it can carry a host path or a fragment of a prompt.
            _logger.LogError(exception, "Development workflow dev-task node run {NodeRunId} of run {RunId} could not be advanced.", nodeRun.Id, run.Id);
            return await _retries.SettleFailureAsync(store,
                    graph,
                    run,
                    nodeRun,
                    nodeRuns,
                    new DevWorkflowFailure(DevWorkflowFailureClasses.Internal,
                        "This node run's development task stopped on an unexpected error. The engine log has the detail.",
                        Output(nodeRun, task, taskId, DevWorkflowFailureClasses.Internal)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Asks Dev Mode for a new coder round on a task the workflow has just been told is wrong, and answers 1
    ///     because a stage boundary is progress. The node run is NOT settled: it stays Running on the same attempt and
    ///     the next poll finds the task at <c>ChangesRequested</c>, where the ordinary next-action path starts the new
    ///     coder attempt with no special casing at all.
    ///     <para>
    ///         One ask per attempt, and the ledger is what enforces it: the transition is written under an operation id
    ///         keyed on <c>(run, node key, attempt)</c>, which <see cref="UnconsumedPriorFailureAsync" /> reads before
    ///         the branch is taken. That matters because the routed failure stays on the input for the life of the
    ///         attempt, so the SECOND round's arrival back at <c>AwaitingApply</c> would otherwise ask again forever.
    ///     </para>
    /// </summary>
    private async Task<int> RequestChangesAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        IDevelopmentStore development,
        DevelopmentTaskSnapshot task,
        string failingNodeKey,
        CancellationToken cancellationToken)
    {
        // A workflow-driven change request does NOT consume a review round. Rounds are spent ENTERING review — both
        // TransitionTaskAsync and FinalizeValidationAsync bump CurrentReviewRound only on the InReview hop, and refuse
        // it past the maximum — and this transition never enters review, so charging it one would take a round away
        // from work that has not been reviewed yet. What it must not do is ask for a round that cannot finish: a task
        // that has spent them all would run a whole coder attempt, reach validation, and be blocked at the review hop
        // for a reason nobody can act on. So the node stands down here instead, while the reason is still legible.
        if (task.CurrentReviewRound >= task.MaxReviewRounds)
        {
            return await _retries.SettleFailureAsync(store,
                    graph,
                    run,
                    nodeRun,
                    nodeRuns,
                    new DevWorkflowFailure(DevWorkflowFailureClasses.BudgetExhausted,
                        string.Create(CultureInfo.InvariantCulture,
                            $"Node run '{failingNodeKey}' asked this task to be implemented again, and it has already used all {task.MaxReviewRounds} of its review rounds."),
                        Output(nodeRun, task, task.Id, DevWorkflowFailureClasses.BudgetExhausted)),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var reason = await DescribePriorFailureAsync(store, run, nodeRun, failingNodeKey, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await development.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(task.Id,
                                     DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "devtask-request-changes"),
                                     DevelopmentTaskStatus.ChangesRequested,
                                     task.Version,
                                     reason),
                                 cancellationToken)
                             .ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception) when (exception is DevelopmentConcurrencyException or DevelopmentInvalidTransitionException)
        {
            // Something else moved the task between this tick's read and its ask — an operator in the Development
            // views, or a sibling tick. Nothing is owed; the next tick reads what it became.
            return 0;
        }
    }

    /// <summary>
    ///     The routed failure this node-run attempt has not answered yet, or nothing.
    ///     <para>
    ///         The routed failure stays on <c>InputJson</c> for the life of the attempt — nothing clears it — so
    ///         whether this attempt has already asked for rework cannot be read off the input. It is read off the
    ///         LEDGER: the change request is written under an operation keyed on <c>(run, node key, attempt)</c>, so
    ///         that operation existing IS the record that this attempt has spent its one ask. Without this the second
    ///         round's arrival back at <c>AwaitingApply</c> would ask again, be answered by the memoized operation,
    ///         move nothing, and leave the node run Running for as long as the dispatcher keeps polling it.
    ///     </para>
    /// </summary>
    private static async Task<string?> UnconsumedPriorFailureAsync(IDevelopmentStore development,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        if (PriorFailureNode(nodeRun.InputJson) is not { } failingNodeKey)
        {
            return null;
        }

        return await development.FindOperationAsync(projectId,
                       DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "devtask-request-changes"),
                       DevelopmentOperationPhases.Completed,
                       cancellationToken)
                   .ConfigureAwait(false) is null
            ? failingNodeKey
            : null;
    }

    /// <summary>
    ///     The node key of the routed failure this dispatch is carrying, or nothing when there is none.
    ///     <para>
    ///         Best-effort by design, like <see cref="Brief" />: an input this cannot read is an input with no routed
    ///         failure in it, which falls through to the branch that was there before rather than throwing on a
    ///         document nobody promised the shape of.
    ///     </para>
    /// </summary>
    private static string? PriorFailureNode(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(inputJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("priorFailureNode", out var node)
                   && node.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(node.GetString())
                ? node.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Why the workflow is asking for another round, in the words of the node that refused this one: which commands
    ///     failed and the tail of what they printed, read off that node's latest validation report.
    ///     <para>
    ///         The report is the evidence, not the routed <c>priorFailure</c> summary — that carries only counts, and a
    ///         coder told "1 of 4 commands failed" has been told nothing it can act on. When the report cannot be read,
    ///         or carries material the sanitizer refuses, the counts are what is left and they are still true.
    ///     </para>
    /// </summary>
    private async Task<string> DescribePriorFailureAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        string failingNodeKey,
        CancellationToken cancellationToken)
    {
        var counts = DescribeCounts(nodeRun.InputJson, failingNodeKey);
        try
        {
            if (await ReadValidationReportAsync(store, run, failingNodeKey, cancellationToken).ConfigureAwait(false) is not { } report)
            {
                return counts;
            }

            var sanitized = DevelopmentArtifactSanitizer.SanitizeText(Describe(report, failingNodeKey, counts));
            return sanitized.Length <= MaxChangeRequestReason ? sanitized : sanitized[..MaxChangeRequestReason];
        }
        catch (Exception exception) when (exception is DevelopmentWorkspaceSecurityException or JsonException or NotSupportedException)
        {
            // A report whose text the sanitizer refuses, or whose shape this build cannot read. The counts are authored
            // here from numbers, so they are always safe to state.
            _logger.LogDebug(exception, "Development workflow node run {NodeRunId} could not quote node '{NodeKey}' validation report.", nodeRun.Id, failingNodeKey);
            return counts;
        }
    }

    /// <summary>The failing node's latest validation report, or nothing when there is none this build can read.</summary>
    private async Task<DevWorkflowValidationReport?> ReadValidationReportAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        string failingNodeKey,
        CancellationToken cancellationToken)
    {
        if ((await store.ListArtifactsAsync(run.Id, cancellationToken: cancellationToken).ConfigureAwait(false))
            .Where(artifact => artifact is { IsValid: true, Kind: DevWorkflowArtifactKind.ValidationReport }
                               && string.Equals(artifact.ProducingNodeKey, failingNodeKey, StringComparison.Ordinal))
            .MaxBy(static artifact => artifact.Sequence) is not { } latest)
        {
            return null;
        }

        var read = await _blobs.ReadAsync(run.Id, latest.Id, latest.ContentSha256, latest.SizeBytes, cancellationToken).ConfigureAwait(false);
        return read.Status == DevWorkflowArtifactReadStatus.Found
            ? JsonSerializer.Deserialize<DevWorkflowValidationReport>(read.Content.Span, JsonOptions)
            : null;
    }

    /// <summary>The report as a rework brief: the gate's own verdict, then each failing command and the tail it left.</summary>
    private static string Describe(DevWorkflowValidationReport report, string failingNodeKey, string counts)
    {
        var lines = new List<string>
        {
            report.FailureDetail is { Length: > 0 } detail
                ? $"Node '{failingNodeKey}' rejected this implementation: {detail}"
                : $"Node '{failingNodeKey}' rejected this implementation. {counts}"
        };

        foreach (var command in (report.Commands ?? []).Where(static command => command.ExitCode != 0 || command.TestOutcome is { Failed: > 0 }))
        {
            lines.Add(command.TestOutcome is { Parsed: true } outcome
                ? string.Create(CultureInfo.InvariantCulture, $"- {command.CommandId} exited {command.ExitCode}, {outcome.Failed} of {outcome.Executed} tests failed:")
                : string.Create(CultureInfo.InvariantCulture, $"- {command.CommandId} exited {command.ExitCode}:"));
            if (Tail(string.IsNullOrWhiteSpace(command.StandardError) ? command.StandardOutput : command.StandardError) is { Length: > 0 } tail)
            {
                lines.Add(tail);
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>The END of a command's output: a failure states itself last, and the head is the run-up to it.</summary>
    private static string Tail(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        return output.Length <= MaxQuotedCommandOutput ? output.Trim() : string.Concat("…", output[^MaxQuotedCommandOutput..].Trim());
    }

    /// <summary>
    ///     What the ROUTED payload says, which is only counts — the fallback when the report itself cannot be quoted.
    ///     Authored from numbers this engine wrote, so there is nothing in it to sanitize.
    /// </summary>
    private static string DescribeCounts(string? inputJson, string failingNodeKey)
    {
        var generic = $"Node '{failingNodeKey}' rejected this implementation and asked for it to be done again.";
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return generic;
        }

        try
        {
            using var document = JsonDocument.Parse(inputJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("priorFailure", out var failure)
                || failure.ValueKind != JsonValueKind.Object)
            {
                return generic;
            }

            var commandsFailed = Number(failure, "commandsFailed");
            var commandsRun = Number(failure, "commandsRun");
            var testsFailed = Number(failure, "testsFailed");
            return string.Create(CultureInfo.InvariantCulture,
                $"{generic} {commandsFailed} of {commandsRun} commands failed, {testsFailed} tests failed.");
        }
        catch (JsonException)
        {
            return generic;
        }
    }

    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    /// <summary>What a landed attempt failed with, in the terms the retry decision is made on.</summary>
    private static DevWorkflowFailure Failure(DevelopmentAttemptSnapshot attempt,
        DevWorkflowNodeRunSnapshot nodeRun,
        DevelopmentTaskSnapshot task,
        Guid taskId)
    {
        // A workspace policy refusing the attempt's diff is not the provider failing: it is the engine declining work on
        // evidence, so it goes straight to a human instead of spending three more attempts to be refused identically.
        var refused = DevelopmentAttemptEvidenceException.Names(attempt.TerminalReason, DevelopmentAttemptFailureCodes.WorkspacePolicyRefused)
            ? DevWorkflowFailureClasses.Policy
            : DevWorkflowFailureClasses.ProviderError;
        var failureClass = attempt.Status == DevelopmentAttemptStatus.Interrupted ? DevWorkflowFailureClasses.Interrupted : refused;
        return new DevWorkflowFailure(failureClass,
            attempt.TerminalReason ?? $"The development {attempt.Role} attempt this node run was driving did not succeed.",
            Output(nodeRun, task, taskId, failureClass));
    }

    /// <summary>
    ///     Dev Mode's two scoped services, or nothing when Development Mode is switched off — in which case it is not
    ///     registered at all, which is why these are asked for rather than injected.
    /// </summary>
    private (IDevelopmentStore? Development, IDevelopmentManagementService? Management) Resolve() =>
        (_services.GetService<IDevelopmentStore>(), _services.GetService<IDevelopmentManagementService>());

    private async Task<int> BlockAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        string failureClass,
        string sanitizedReason,
        CancellationToken cancellationToken) =>
        await _retries.SettleFailureAsync(store,
                graph,
                run,
                nodeRun,
                nodeRuns,
                new DevWorkflowFailure(failureClass, sanitizedReason, Output(nodeRun, task: null, nodeRun.DevelopmentTaskId, failureClass)),
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task<int> SettleAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        DevWorkflowNodeRunStatus target,
        string? failureClass,
        string? terminalReason,
        string outputJson,
        CancellationToken cancellationToken)
    {
        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, target, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               DevWorkflowVersions.Any,
                               target,
                               OutputJson: outputJson,
                               FailureClass: failureClass,
                               TerminalReason: terminalReason,
                               WorkItemStatus: DevWorkflowStateMachine.WorkItemStatusAfter(run.Status, nodeRuns, nodeRun.Id, target)),
                           cancellationToken)
                       .ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    ///     The implementation node's slice of the output document every executor writes: the verdict a conditional edge
    ///     routes on, and the task a reader drills into. No patch and no evidence — those live on the development task,
    ///     which is exactly why the node run names it.
    /// </summary>
    private static string Output(DevWorkflowNodeRunSnapshot nodeRun, DevelopmentTaskSnapshot? task, Guid? taskId, string? failureClass) =>
        JsonSerializer.Serialize(new DevTaskOutput(task is { Status: DevelopmentTaskStatus.AwaitingApply or DevelopmentTaskStatus.Completed }
                ? DevWorkflowNodeOutputStatuses.Succeeded
                : DevWorkflowNodeOutputStatuses.Failed,
            nodeRun.Attempt,
            failureClass,
            taskId,
            task?.Status.ToString(),
            task?.CurrentReviewRound),
            JsonOptions);

    /// <summary>
    ///     What a materialized child's input document says about the task it is there to implement.
    ///     <see cref="Requirements" /> is required; the other two have somewhere honest to fall back to.
    /// </summary>
    private sealed record DevTaskBrief(string? Title, string? Requirements, string? AcceptanceCriteriaJson);

    private sealed record DevTaskOutput(
        string Status,
        int Attempt,
        string? FailureClass,
        Guid? DevelopmentTaskId,
        string? TaskStatus,
        int? ReviewRound);
}
