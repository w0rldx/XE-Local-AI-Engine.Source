namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Globalization;
using System.Runtime.InteropServices;
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

    /// <summary>
    ///     How much rule-set text a DevTask node run may put in front of Dev Mode's coder and reviewer, in characters.
    ///     <para>
    ///         The same 4096 as <see cref="MaxChangeRequestReason" /> and as a rule set's own body cap, and for the same
    ///         reason: the prompt these sections land in already carries the task's title, requirements and acceptance
    ///         criteria uncapped, so policy gets a bounded share of it rather than the room it would like. Past this the
    ///         sections are truncated visibly, never silently dropped.
    ///     </para>
    /// </summary>
    private const int MaxPolicyCharacters = 4096;

    /// <summary>How much of one failing command's captured output the change request quotes, from the END of it.</summary>
    private const int MaxQuotedCommandOutput = 800;

    /// <summary>
    ///     The last resort: a sentence with nothing interpolated into it, for when even the routed counts cannot be
    ///     stated safely. It is still true, which is the only bar a reason has to clear.
    /// </summary>
    private const string GenericChangeRequest = "A downstream check rejected this implementation and asked for it to be done again.";

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

            // The rule sets this node run RECORDED, put where Dev Mode's own prompts read them. On EVERY dispatch, not
            // only the first bind: the operation id is keyed to this node-run ATTEMPT, so a replayed tick meets the
            // store's idempotency, while a fix loop that routes the node run back around re-applies the policy the
            // settle below cleared. INSIDE the try, because a task deleted between the resolve and this write is the
            // same decided fact the catch below stands the node down for.
            await RecordPolicyAsync(development, run, nodeRun, taskId, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    ///     Puts the node run's recorded rule-set text on the task it drives, as the one channel Dev Mode's coder and
    ///     reviewer prompts read policy through — the same event-derived route <c>PreviousRoundFeedback</c> travels, so
    ///     it costs no column and no migration.
    ///     <para>
    ///         Nothing applied records an EMPTY resolution rather than nothing at all — and so does a resolution whose
    ///         bodies are all missing or all too long to fit, the render being what decides and what logs each section
    ///         it dropped. Writing nothing was the leak: the snapshot answers off the latest row, so a workflow that
    ///         resolved no policy would leave the PREVIOUS one governing rounds it never applied to.
    ///     </para>
    /// </summary>
    private async Task RecordPolicyAsync(IDevelopmentStore development,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        var applied = DevWorkflowRulePolicyResolver.Read(nodeRun.PolicyResolutionJson);
        var policyText = applied.Count == 0
            ? string.Empty
            : DevWorkflowPolicyText.Render(applied, MaxPolicyCharacters, occupied: 0, nodeRun.Id, _logger).Trim();
        await WritePolicyAsync(development,
                run,
                nodeRun,
                taskId,
                policyText,
                policyText.Length == 0
                    ? []
                    : [.. applied.Select(entry => new DevelopmentWorkflowRuleSetReference(entry.Id, entry.Name, entry.ContentSha256))],
                "devtask-policy",
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Revokes the injection when this node run stops driving the task, so what governed the workflow's rounds does
    ///     not go on governing the operator's own later ones. Called from the shared writers that settle, block and
    ///     cancel a node run rather than from their call sites — every terminal path has to revoke, and naming them one
    ///     by one is how the attempt-cancelled settle, the stand-downs and the run cancel were missed.
    ///     <para>
    ///         Deliberately OVER-EAGER: a failure the retry policy re-attempts increments the node run's attempt, so the
    ///         re-dispatch derives a new operation id and records the policy again. A PAUSE is the one stop that does
    ///         not — it parks the row at the same attempt, whose operation id is already written — which is why only the
    ///         cancelling half of the stop clears.
    ///     </para>
    ///     <para>
    ///         Best-effort by construction: a node run that never bound a task, and a node whose Development Mode is
    ///         switched off, have no injection to revoke.
    ///     </para>
    /// </summary>
    private async Task ClearPolicyAsync(DevWorkflowRunSnapshot run, DevWorkflowNodeRunSnapshot nodeRun, CancellationToken cancellationToken)
    {
        if (nodeRun.DevelopmentTaskId is not { } taskId || Resolve().Development is not { } development)
        {
            return;
        }

        await WritePolicyAsync(development, run, nodeRun, taskId, string.Empty, [], "devtask-policy-clear", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     One policy write, keyed to this node-run ATTEMPT and phase: a replayed tick meets the store's idempotency and
    ///     appends nothing, while the next attempt of the same node run writes its own row.
    /// </summary>
    private static async Task WritePolicyAsync(IDevelopmentStore development,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        Guid taskId,
        string policyText,
        IReadOnlyList<DevelopmentWorkflowRuleSetReference> ruleSets,
        string phase,
        CancellationToken cancellationToken)
    {
        _ = await development.RecordWorkflowPolicyAsync(taskId,
                                 DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, phase),
                                 policyText,
                                 ruleSets,
                                 cancellationToken)
                             .ConfigureAwait(false);
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
        if (cancel)
        {
            // Only the cancel. A pause parks the row at the SAME attempt, so the resume's re-dispatch re-derives an
            // operation id the store has already written and would record nothing — clearing here would leave the
            // resumed round ungoverned.
            await ClearPolicyAsync(run, nodeRun, cancellationToken).ConfigureAwait(false);
        }

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
            case DevelopmentTaskStatus.AwaitingApply when rework is { } routed:

                // The fix loop landed on an already-approved task. Settling it Succeeded here is what made a routed
                // re-attempt a no-op: the loop would route, re-succeed in the same tick, and spend the target's whole
                // attempt budget in seconds without ever asking for a different patch. So the node asks Dev Mode for
                // rework instead, with the routed node's own validation report as the review evidence.
                return await RequestChangesAsync(store, graph, run, nodeRun, nodeRuns, development, task, routed, cancellationToken).ConfigureAwait(false);

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
                return await SettleFailureAsync(store,
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
                : await SettleFailureAsync(store,
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

        // The person who retried this node said WHY, and the coder about to redo the round is the one who needs to
        // hear it. It travels the same route a routed rejection does, and for the same reason: a task's own change
        // request is the one channel Dev Mode composes a coder prompt out of. The node run is not settled — the next
        // poll finds the task at ChangesRequested, where the ordinary next-action path starts the round it was going
        // to start anyway.
        if (await CarryOperatorRetryAsync(development, run, nodeRun, projectId, task, attempts, cancellationToken).ConfigureAwait(false))
        {
            return 1;
        }

        return await StartNextActionAsync(store, graph, run, nodeRun, nodeRuns, development, management, projectId, taskId, task, attempts.Count, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Hands the operator's reason for retrying this node run to the task's next coder round, and answers whether
    ///     it wrote one. Marked <c>OperatorDirected</c>, which is what lets the coder and reviewer prompts rank a
    ///     person's sentence above the task's own immutable requirements and above a reviewer's feedback.
    ///     <para>
    ///         Only from <c>InProgress</c> with a coder attempt that did NOT succeed, which is where a blocked-then-
    ///         retried implementation lands: the next action there is a coder round either way, so the change request
    ///         adds the sentence without changing what the retry does. Every other case is a KNOWN HOLE — the retry
    ///         still happens, the operator's sentence simply does not reach a coder — and each is deliberate:
    ///     </para>
    ///     <list type="bullet">
    ///         <item><c>InProgress</c> whose last coder attempt SUCCEEDED is on its way to deterministic validation;
    ///             asking for changes would throw that round and its evidence away.</item>
    ///         <item><c>Ready</c> and <c>Planned</c> have no round to brief — nothing has been implemented yet.</item>
    ///         <item><c>ChangesRequested</c> already carries the verdict that asked for the round — a reviewer's
    ///             feedback, or the deterministic gate's failure — and overwriting it would replace the very thing the
    ///             round exists to answer.</item>
    ///         <item><c>InReview</c> would have its next action CHANGED by the ask, from the re-review that is the
    ///             right answer to a reviewer that failed into a coder round nobody asked for.</item>
    ///         <item><c>AwaitingApply</c> never reaches here: the caller settles the node run <c>Succeeded</c> above,
    ///             because an approved implementation waiting to be applied is still a true claim.</item>
    ///         <item><c>Blocked</c> never reaches here either — the caller stands the node run down first — and the
    ///             task's transition table has no outbound edge from it, so nothing could be written anyway.</item>
    ///         <item><c>Validation</c> is Dev Mode's own supervisor window and holds no attempt; <c>Completed</c> and
    ///             <c>Cancelled</c> are settled above. None of the three is a round anything can brief.</item>
    ///     </list>
    ///     <para>
    ///         One ask per (node run, ATTEMPT), and the ledger is what enforces it: the reason stays on the inputs for
    ///         the life of the attempt, and the round asked for walks the task back through <c>InProgress</c> without
    ///         starting an attempt this node run is answerable for — so without the operation id that tick would ask a
    ///         second time and loop the task back to <c>ChangesRequested</c> for as long as the poll continues. The
    ///         reason itself is scoped by attempt on the way in, so a later automatic re-attempt quotes nobody.
    ///     </para>
    /// </summary>
    private static async Task<bool> CarryOperatorRetryAsync(IDevelopmentStore development,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        Guid projectId,
        DevelopmentTaskSnapshot task,
        IReadOnlyList<DevelopmentAttemptSnapshot> attempts,
        CancellationToken cancellationToken)
    {
        if (DevWorkflowNodeInputs.OperatorRetryReasonFor(nodeRun.InputJson, nodeRun.Attempt) is not { } said
            || task.Status != DevelopmentTaskStatus.InProgress
            || attempts.LastOrDefault(static attempt => attempt.Role == DevelopmentAttemptRole.Coder) is { Status: DevelopmentAttemptStatus.Succeeded })
        {
            return false;
        }

        var operationId = DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "devtask-operator-retry");
        if (await development.FindOperationAsync(projectId, operationId, DevelopmentOperationPhases.Completed, cancellationToken).ConfigureAwait(false) is not null)
        {
            return false;
        }

        try
        {
            _ = await development.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(task.Id,
                                     operationId,
                                     DevelopmentTaskStatus.ChangesRequested,
                                     task.Version,
                                     $"An operator retried the '{nodeRun.NodeKey}' step of the workflow driving this task, and said: {said}",
                                     OperatorDirected: true),
                                 cancellationToken)
                             .ConfigureAwait(false);
            return true;
        }
        catch (DevelopmentConcurrencyException)
        {
            // Something else moved the task between this tick's read and its ask — an operator in the Development
            // views, or a sibling tick. Nothing is owed, and the operation id is still unwritten: the next tick reads
            // what the task became and asks again if that is still the right thing to do.
            //
            // ONLY the concurrency case. TransitionTaskAsync checks the version BEFORE legality, so a task whose
            // status moved always surfaces here rather than as an invalid transition — which means an invalid
            // transition is a broken invariant in the table above, not a race. Swallowing it would start the coder
            // round unbriefed and call that success; propagating it stalls this node run loudly (the dispatcher's
            // AdvanceSafelyAsync logs it at Error and re-derives from unchanged rows next tick).
            return false;
        }
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
            return await SettleFailureAsync(store,
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
    ///         One ask per ROUTE, and the ledger is what enforces it: the transition is written under an operation id
    ///         keyed on the route rather than on this node run's own attempt, which
    ///         <see cref="UnconsumedPriorFailureAsync" /> reads before the branch is taken. That matters because the
    ///         routed failure stays on the input for the life of the attempt AND across the node's own retries, so the
    ///         SECOND round's arrival back at <c>AwaitingApply</c> would otherwise ask again forever.
    ///     </para>
    /// </summary>
    private async Task<int> RequestChangesAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        IDevelopmentStore development,
        DevelopmentTaskSnapshot task,
        RoutedFailure routed,
        CancellationToken cancellationToken)
    {
        var failingNodeKey = routed.NodeKey;
        // A workflow-driven change request does NOT consume a round. TransitionTaskAsync bumps CurrentReviewRound only
        // on the InReview hop, and refuses it past the maximum, and this transition never enters review — so charging
        // it one would take a round away from work nothing has judged yet. FinalizeValidationAsync DOES charge one on
        // its failure hop, because a failed deterministic gate IS a judgement on the round, and that budget is the only
        // thing bounding the rework loop; this ask judges nothing. What it must not do is ask for a round that cannot
        // finish: a task that has spent them all would run a whole coder attempt and be stood down before it could
        // reach a review, for a reason nobody can act on. So the node stands down here instead, while the reason is
        // still legible.
        if (task.CurrentReviewRound >= task.MaxReviewRounds)
        {
            return await SettleFailureAsync(store,
                    graph,
                    run,
                    nodeRun,
                    nodeRuns,
                    new DevWorkflowFailure(DevWorkflowFailureClasses.BudgetExhausted,
                        string.Create(CultureInfo.InvariantCulture,
                            $"Node run '{failingNodeKey}' asked this task to be implemented again, and it has already used all {task.MaxReviewRounds} of its rounds."),
                        Output(nodeRun, task, task.Id, DevWorkflowFailureClasses.BudgetExhausted)),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var reason = await DescribePriorFailureAsync(store, run, nodeRun, routed, cancellationToken).ConfigureAwait(false);
        if (!reason.Evidenced)
        {
            // No readable validation report and no command or test that actually ran. Asking for a round on that is
            // how a coder is told to redo approved work for no stated reason; SUCCEEDING on it instead would send the
            // run straight back round the loop to re-fail the same check, spending the budget on having tried nothing.
            // So it stands the node down where a human can read why, with the approved task untouched behind it.
            _logger.LogWarning(
                "Development workflow node run {NodeRunId} carries a routed failure from node '{NodeKey}' with no validation report and no command counts behind it, so the node is stood down instead of asking for another round.",
                nodeRun.Id,
                failingNodeKey);
            return await SettleFailureAsync(store,
                    graph,
                    run,
                    nodeRun,
                    nodeRuns,
                    new DevWorkflowFailure(DevWorkflowFailureClasses.Configuration,
                        $"Node '{failingNodeKey}' routed a failure here but left no validation report or failing counts to act on, so there is nothing to ask for a new round about.",
                        Output(nodeRun, task, task.Id, DevWorkflowFailureClasses.Configuration)),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            _ = await development.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(task.Id,
                                     ChangeRequestOperationId(run, nodeRun, routed),
                                     DevelopmentTaskStatus.ChangesRequested,
                                     task.Version,
                                     reason.Reason),
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
    ///     The routed failure this node run has not answered yet, or nothing.
    ///     <para>
    ///         The routed failure stays on <c>InputJson</c> for the life of the attempt — nothing clears it — so
    ///         whether it has already been answered cannot be read off the input. It is read off the LEDGER: the change
    ///         request is written under an operation keyed on the ROUTE, so that operation existing IS the record that
    ///         this rejection has had its one ask. Without it the second round's arrival back at
    ///         <c>AwaitingApply</c> would ask again, be answered by the memoized operation, move nothing, and leave the
    ///         node run Running for as long as the dispatcher keeps polling it.
    ///     </para>
    /// </summary>
    private static async Task<RoutedFailure?> UnconsumedPriorFailureAsync(IDevelopmentStore development,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        if (RoutedFailureOf(nodeRun.InputJson) is not { } routed)
        {
            return null;
        }

        return await development.FindOperationAsync(projectId,
                       ChangeRequestOperationId(run, nodeRun, routed),
                       DevelopmentOperationPhases.Completed,
                       cancellationToken)
                   .ConfigureAwait(false) is null
            ? routed
            : null;
    }

    /// <summary>
    ///     The ledger id for this route's one change request, keyed on the ROUTE — the node that refused and the
    ///     attempt of it that did — rather than on the target's own attempt.
    ///     <para>
    ///         The target's attempt is the wrong key because it MOVES while the same rejection is still outstanding: a
    ///         transient failure between the change request and the round it asked for spends one of the target's
    ///         attempts, and the next arrival back at <c>AwaitingApply</c> then looked for an id nothing had written.
    ///         The rejection was answered twice — a second coder round against work a reviewer had already approved,
    ///         which is how an approved patch is discarded and the task's review rounds run out.
    ///     </para>
    ///     <para>
    ///         The failing node's key rides in the phase, so two checks that both route here are two routes and get one
    ///         ask each. A payload from before this shipped carries no attempt: it keeps the id it was written under,
    ///         because changing the key of a route already in flight would ask for its round a second time.
    ///     </para>
    /// </summary>
    private static Guid ChangeRequestOperationId(DevWorkflowRunSnapshot run, DevWorkflowNodeRunSnapshot nodeRun, RoutedFailure routed) =>
        routed.Attempt is { } attempt
            ? DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, attempt, $"devtask-request-changes:{routed.NodeKey}")
            : DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "devtask-request-changes");

    /// <summary>One routed rejection: the node whose verdict sent the run back, and the attempt of it that reached that verdict.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct RoutedFailure(string NodeKey, int? Attempt);

    /// <summary>
    ///     The routed failure this dispatch is carrying — the node that refused and the attempt of it that did — or
    ///     nothing when there is none.
    ///     <para>
    ///         Best-effort by design, like <see cref="Brief" />: an input this cannot read is an input with no routed
    ///         failure in it, which falls through to the branch that was there before rather than throwing on a
    ///         document nobody promised the shape of. The attempt is optional for the same reason plus one more — a
    ///         payload written before it existed has none, and is answered exactly as it used to be.
    ///     </para>
    /// </summary>
    private static RoutedFailure? RoutedFailureOf(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(inputJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("priorFailureNode", out var node)
                || node.ValueKind != JsonValueKind.String
                || node.GetString() is not { } nodeKey
                || string.IsNullOrWhiteSpace(nodeKey))
            {
                return null;
            }

            return new RoutedFailure(nodeKey,
                document.RootElement.TryGetProperty("priorFailureAttempt", out var attempt)
                && attempt.ValueKind == JsonValueKind.Number
                && attempt.TryGetInt32(out var number)
                    ? number
                    : null);
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
    private async Task<PriorFailureReason> DescribePriorFailureAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        RoutedFailure routed,
        CancellationToken cancellationToken)
    {
        var failingNodeKey = routed.NodeKey;
        // The counts go through the same bound and the same sanitizer as the report does: the node key interpolated
        // into them comes from a stored graph definition, which is authored text like any other.
        var (countsText, hasCounts) = DescribeCounts(nodeRun.InputJson, failingNodeKey);
        var counts = Bounded(countsText, GenericChangeRequest);
        try
        {
            return await ReadValidationReportAsync(store, run, routed, cancellationToken).ConfigureAwait(false) is { } report
                ? new PriorFailureReason(Bounded(Describe(report, failingNodeKey, counts), counts), Evidenced: true)
                : new PriorFailureReason(counts, hasCounts);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            // A report whose shape this build cannot read, or whose bytes the filesystem would not hand over — the blob
            // store answers a MISSING or tampered blob with a status, but a disk or permission fault still throws, and
            // letting it escape would fail the tick and re-throw on every sweep after it. The counts are authored here
            // from numbers, so they are always the safe answer.
            _logger.LogDebug(exception, "Development workflow node run {NodeRunId} could not quote node '{NodeKey}' validation report.", nodeRun.Id, failingNodeKey);
            return new PriorFailureReason(counts, hasCounts);
        }
    }

    /// <summary>
    ///     One rework reason and whether anything actually judged the work behind it — a readable validation report, or
    ///     a routed payload carrying a command or test that really ran. A reason with neither is a sentence, not a
    ///     verdict, and nothing may spend a coder round on it.
    /// </summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct PriorFailureReason(string Reason, bool Evidenced);

    /// <summary>
    ///     One reason, sanitized and bounded, or <paramref name="fallback" /> when the sanitizer refuses it. Every
    ///     string that reaches a task's rework reason goes through here: the report quotes model-adjacent command
    ///     output, and the counts quote a node key an operator wrote.
    /// </summary>
    private static string Bounded(string text, string fallback)
    {
        try
        {
            var sanitized = DevelopmentArtifactSanitizer.SanitizeText(text);
            return sanitized.Length <= MaxChangeRequestReason ? sanitized : sanitized[..MaxChangeRequestReason];
        }
        catch (DevelopmentWorkspaceSecurityException)
        {
            return fallback;
        }
    }

    /// <summary>
    ///     The report the ROUTED attempt wrote, or nothing when there is none this build can read.
    ///     <para>
    ///         Correlated to the attempt, not merely to the node key. A later attempt of that node can refuse before it
    ///         runs anything — a missing command profile, a workspace it could not prepare — and write no report at
    ///         all; the latest report for the key is then the PREVIOUS attempt's, about an implementation that has
    ///         since been rewritten. Quoting it makes the reason look evidenced and asks a coder to fix output nothing
    ///         just produced. Refusing it drops through to the counts, which for a check that ran nothing say so, and
    ///         the node stands down where a human can read why.
    ///     </para>
    /// </summary>
    private async Task<DevWorkflowValidationReport?> ReadValidationReportAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        RoutedFailure routed,
        CancellationToken cancellationToken)
    {
        if ((await store.ListArtifactsAsync(run.Id, cancellationToken: cancellationToken).ConfigureAwait(false))
            .Where(artifact => artifact is { IsValid: true, Kind: DevWorkflowArtifactKind.ValidationReport }
                               && string.Equals(artifact.ProducingNodeKey, routed.NodeKey, StringComparison.Ordinal))
            .MaxBy(static artifact => artifact.Sequence) is not { } latest)
        {
            return null;
        }

        var read = await _blobs.ReadAsync(run.Id, latest.Id, latest.ContentSha256, latest.SizeBytes, cancellationToken).ConfigureAwait(false);
        if (read.Status != DevWorkflowArtifactReadStatus.Found)
        {
            return null;
        }

        var report = JsonSerializer.Deserialize<DevWorkflowValidationReport>(read.Content.Span, JsonOptions);

        // A route from before the attempt travelled on the payload names no attempt, and is answered as it used to be.
        return routed.Attempt is not { } attempt || report?.Attempt == attempt ? report : null;
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
    ///     Authored from numbers this engine wrote, so there is nothing in it to sanitize. <c>HasCounts</c> is false
    ///     when nothing ran, which is the difference between a verdict and a sentence that sounds like one.
    /// </summary>
    private static (string Text, bool HasCounts) DescribeCounts(string? inputJson, string failingNodeKey)
    {
        (string Text, bool HasCounts) generic = ($"Node '{failingNodeKey}' rejected this implementation and asked for it to be done again.", false);
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

            // Nothing ran, so there is nothing to count. The sentence this used to author — "0 of 0 commands failed, 0
            // tests failed" — was read live on 2026-09-02 by a coder being asked to redo approved work, and it says
            // less than the generic line does while sounding like a measurement.
            return commandsRun <= 0 && commandsFailed <= 0 && testsFailed <= 0
                ? generic
                : (string.Create(CultureInfo.InvariantCulture,
                    $"{generic.Text} {commandsFailed} of {commandsRun} commands failed, {testsFailed} tests failed."), true);
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
        await SettleFailureAsync(store,
                graph,
                run,
                nodeRun,
                nodeRuns,
                new DevWorkflowFailure(failureClass, sanitizedReason, Output(nodeRun, task: null, nodeRun.DevelopmentTaskId, failureClass)),
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    ///     The one door this executor hands a failing node run to the retry policy through, so the policy revocation
    ///     sits with the write instead of being remembered at each of the six call sites.
    /// </summary>
    private async Task<int> SettleFailureAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        DevWorkflowFailure failure,
        CancellationToken cancellationToken)
    {
        await ClearPolicyAsync(run, nodeRun, cancellationToken).ConfigureAwait(false);
        return await _retries.SettleFailureAsync(store, graph, run, nodeRun, nodeRuns, failure, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> SettleAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        DevWorkflowNodeRunStatus target,
        string? failureClass,
        string? terminalReason,
        string outputJson,
        CancellationToken cancellationToken)
    {
        await ClearPolicyAsync(run, nodeRun, cancellationToken).ConfigureAwait(false);
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
