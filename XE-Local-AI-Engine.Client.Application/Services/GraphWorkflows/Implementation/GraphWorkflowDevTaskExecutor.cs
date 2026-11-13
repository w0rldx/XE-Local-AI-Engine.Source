namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     The <c>DevTask</c> lane: <c>implement</c> drives one Development Mode task to <c>AwaitingApply</c>, <c>apply</c>
///     puts that task's patch through Dev Mode's own apply gate on behalf of this run.
/// </summary>
/// <remarks>
///     Holds nothing in flight. The task is durable Dev Mode state and the row names it, so the lane waits by being polled
///     on every tick, and a restart resumes by re-reading the pinned task. Development services are resolved per call, so
///     Development Mode off is a node failure, not a DI error. See docs/wiki/21-graph-workflows.md ("DevTask").
/// </remarks>
internal sealed class GraphWorkflowDevTaskExecutor : IGraphWorkflowNodeExecutor
{
    private const string DevelopmentOff = "This node drives a Development task, and Development Mode is switched off on this node.";

    private const string CancelledReason = "The run was cancelled while this node run was driving its Development task.";

    /// <summary>Dev Mode's own default review budget, for a project that somehow carries no task to copy one from.</summary>
    private const int DefaultMaxReviewRounds = 3;

    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    private readonly ILogger<GraphWorkflowDevTaskExecutor> _logger;
    private readonly GraphWorkflowOptions _options;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    public GraphWorkflowDevTaskExecutor(IServiceScopeFactory scopeFactory,
        INodeRuntimeSettings runtimeSettings,
        IOptions<GraphWorkflowOptions> options,
        TimeProvider timeProvider,
        ILogger<GraphWorkflowDevTaskExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        _runtimeSettings = runtimeSettings;
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _options = options.Value;
    }

    public bool Owns(GraphWorkflowNodeKind kind) =>
        kind == GraphWorkflowNodeKind.DevTask;

    /// <summary>Never: the work is a Dev Mode task, not a task of this process, so the cancel drain is answered by the poll.</summary>
    public bool IsInFlight(Guid nodeRunId) =>
        false;

    public Task<int> DispatchAsync(IGraphWorkflowStore store,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraph graph,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken) =>
        DriveAsync(store, run, graph, node, nodeRun, cancellationToken);

    /// <summary>Re-reads the task a <c>Running</c> row drives and settles the row once Dev Mode is done with it; zero while it works.</summary>
    public Task<int> PollAsync(IGraphWorkflowStore store,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraph graph,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken) =>
        DriveAsync(store, run, graph, node, nodeRun, cancellationToken);

    public Task<bool> StopAsync(Guid nodeRunId) =>
        Task.FromResult(false);

    public Task DiscardAsync(Guid nodeRunId) =>
        Task.CompletedTask;

    public Task ForgetSupersededAsync(IReadOnlyList<GraphWorkflowNodeRunSnapshot> nodeRuns) =>
        Task.CompletedTask;

    /// <summary>One pass over a <c>Pending</c> or <c>Running</c> DevTask row, answering how many transitions it wrote.</summary>
    private async Task<int> DriveAsync(IGraphWorkflowStore store,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraph graph,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(nodeRun);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var development = scope.ServiceProvider.GetService<IDevelopmentStore>();
        var management = scope.ServiceProvider.GetService<IDevelopmentManagementService>();

        if (run.Status == GraphWorkflowRunStatus.Cancelling)
        {
            // The cancel drain polls before it settles: stop the attempt this row drives, then end the row here.
            if (development is not null && management is not null && nodeRun.DevelopmentTaskId is { } driven && ProjectId(run) is { } drivenProject)
            {
                await StandDownAsync(development, management, run, node, nodeRun, drivenProject, driven, cancellationToken);
            }

            return await SettleAsync(store, graph, run, node, nodeRun, GraphWorkflowNodeRunStatus.Cancelled, GraphWorkflowFailureClass.Cancelled, CancelledReason, output: null, cancellationToken);
        }

        if (node.Config is not GraphWorkflowDevTaskConfig config)
        {
            return await FailAsync(store, graph, run, node, nodeRun, GraphWorkflowFailureClass.ValidationFailed, $"Node '{node.NodeKey}' is a DevTask node without DevTask settings.", cancellationToken);
        }

        // Registration is the startup switch; the live switch only stops a node from STARTING work, so an attempt in flight finishes.
        if (development is null || management is null
                                || (nodeRun.Status == GraphWorkflowNodeRunStatus.Pending && !await _runtimeSettings.GetDevelopmentEnabledAsync(cancellationToken)))
        {
            return await FailAsync(store, graph, run, node, nodeRun, GraphWorkflowFailureClass.ValidationFailed, DevelopmentOff, cancellationToken);
        }

        if (ProjectId(run) is not { } projectId)
        {
            return await FailAsync(store,
                graph,
                run,
                node,
                nodeRun,
                GraphWorkflowFailureClass.ValidationFailed,
                $"This run's input names no Development project ('{GraphWorkflowInvocationExecutor.DevelopmentProjectIdKey}').",
                cancellationToken);
        }

        try
        {
            return config.IsApply
                ? await ApplyAsync(store, graph, run, node, nodeRun, config, development, management, projectId, cancellationToken)
                : await ImplementAsync(store, graph, run, node, nodeRun, config, development, management, projectId, cancellationToken);
        }
        catch (KeyNotFoundException exception)
        {
            // The project or task is gone, which no re-attempt brings back. Re-read: the row may have opened before the read failed.
            var current = await store.GetNodeRunAsync(run.Id, node.NodeKey, cancellationToken);
            return await FailAsync(store, graph, run, node, current, GraphWorkflowFailureClass.ValidationFailed, Sanitized(exception), cancellationToken);
        }
    }

    /// <summary>Opens the row, creates or re-uses its task, pins it, then asks Dev Mode for the next step.</summary>
    /// <remarks>
    ///     The pin is written BEFORE the first <c>StartNextActionAsync</c>: after a restart the row is <c>Pending</c> again with
    ///     its pointer kept, and the re-dispatch resumes that task. The create is keyed on run and node key without the
    ///     attempt, so a crash between create and pin is answered with the same task, never a second one.
    /// </remarks>
    private async Task<int> ImplementAsync(IGraphWorkflowStore store,
        GraphWorkflowGraph graph,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        GraphWorkflowDevTaskConfig config,
        IDevelopmentStore development,
        IDevelopmentManagementService management,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var written = 0;
        if (nodeRun.Status == GraphWorkflowNodeRunStatus.Pending)
        {
            var inputJson = await InputDocumentAsync(store, graph, node, run, cancellationToken);
            if (!GraphWorkflowInvocationExecutor.TryBuildBoundPrompt(new GraphWorkflowLlmCallConfig
                    {
                        Model = null,
                        SystemPrompt = null,
                        Prompt = config.Requirements ?? string.Empty,
                        InputBindings = config.InputBindings,
                        ReasoningEffort = null,
                        ResponseJsonSchema = null,
                        SamplingOptions = null
                    },
                    inputJson,
                    _options.MaxRunInputBytes,
                    out var requirements,
                    out var bindingError))
            {
                return await FailAsync(store, graph, run, node, nodeRun, GraphWorkflowFailureClass.ValidationFailed, $"Node '{node.NodeKey}' {bindingError}", cancellationToken);
            }

            written += await RunningAsync(store, run, nodeRun, inputJson, cancellationToken);
            var taskId = nodeRun.DevelopmentTaskId ?? await CreateTaskAsync(development, run, node, config, projectId, requirements, cancellationToken);
            if (!await PinAsync(store, run, nodeRun, taskId, cancellationToken))
            {
                // The run stopped being live between the two writes; the next tick's drain settles the row. A task no row owns would stay on the Development page.
                if (nodeRun.DevelopmentTaskId is null)
                {
                    await CancelOrphanAsync(development, run, node, taskId, cancellationToken);
                }

                return written;
            }

            await RecordInstructionsAsync(development, run, node, nodeRun.Attempt, taskId, config.Instructions, cancellationToken);
            nodeRun = await store.GetNodeRunAsync(run.Id, node.NodeKey, cancellationToken);
        }

        if (nodeRun.DevelopmentTaskId is not { } pinned)
        {
            return written + await FailAsync(store,
                graph,
                run,
                node,
                nodeRun,
                GraphWorkflowFailures.Classify(GraphWorkflowFailureClass.Interrupted, nodeRun.Attempt, node.MaxAttempts),
                "This node run was left running without a Development task, so nothing can report what it is doing.",
                cancellationToken);
        }

        // The lane answers an explicit timeoutSeconds itself, before the dispatcher's backstop, because only it can stop the Dev Mode attempt.
        if (GraphWorkflowDeadline.Expiry(node, nodeRun, _options) is { } due && _timeProvider.GetUtcNow() >= due)
        {
            await StandDownAsync(development, management, run, node, nodeRun, projectId, pinned, cancellationToken);
            return written + await SettleAsync(store,
                graph,
                run,
                node,
                nodeRun,
                GraphWorkflowNodeRunStatus.Failed,
                GraphWorkflowFailures.Classify(GraphWorkflowFailureClass.Timeout, nodeRun.Attempt, node.MaxAttempts),
                string.Create(CultureInfo.InvariantCulture,
                    $"This node run did not finish within the {node.TimeoutSeconds} seconds its node allows, so its running Development attempt was cancelled."),
                output: null,
                cancellationToken);
        }

        return written + await AdvanceAsync(store, graph, run, node, nodeRun, development, management, projectId, pinned, cancellationToken);
    }

    /// <summary>Settles the row on what Dev Mode made of the task, asks for the next step when the task is idle, or waits.</summary>
    private async Task<int> AdvanceAsync(IGraphWorkflowStore store,
        GraphWorkflowGraph graph,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        IDevelopmentStore development,
        IDevelopmentManagementService management,
        Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        var task = await development.GetTaskAsync(taskId, cancellationToken);
        var attempts = await development.ListAttemptsAsync(taskId, cancellationToken);
        switch (task.Status)
        {
            case DevelopmentTaskStatus.AwaitingApply or DevelopmentTaskStatus.Completed:
                return await EndImplementAsync(store, graph, run, node, nodeRun, development, task, GraphWorkflowNodeRunStatus.Succeeded, failureClass: null, reason: null, cancellationToken);

            case DevelopmentTaskStatus.Cancelled:
                return await EndImplementAsync(store,
                    graph,
                    run,
                    node,
                    nodeRun,
                    development,
                    task,
                    GraphWorkflowNodeRunStatus.Cancelled,
                    GraphWorkflowFailureClass.Cancelled,
                    "The Development task this node run was driving was cancelled.",
                    cancellationToken);

            case DevelopmentTaskStatus.Blocked:

                // Dev Mode stood the task down (review rounds spent, a missing test runner, an operator): a re-attempt reads the same Blocked task.
                return await EndImplementAsync(store,
                    graph,
                    run,
                    node,
                    nodeRun,
                    development,
                    task,
                    GraphWorkflowNodeRunStatus.Failed,
                    GraphWorkflowFailureClass.ValidationFailed,
                    task.BlockedReason ?? "The Development task this node run was driving was stood down.",
                    cancellationToken);

            default:
                break;
        }

        if (attempts.Any(static attempt => attempt.Status is DevelopmentAttemptStatus.Pending or DevelopmentAttemptStatus.Running))
        {
            // Dev Mode drives its own attempt; the next tick reads what it became.
            return 0;
        }

        // Only attempts THIS row attempt started: an earlier one is that attempt's evidence, not this one's answer. Both stamps come from one clock.
        if (attempts.LastOrDefault(attempt => attempt.StartedAtUtc >= nodeRun.StartedAtUtc) is { Status: not DevelopmentAttemptStatus.Succeeded } landed)
        {
            return landed.Status == DevelopmentAttemptStatus.Cancelled
                ? await EndImplementAsync(store,
                    graph,
                    run,
                    node,
                    nodeRun,
                    development,
                    task,
                    GraphWorkflowNodeRunStatus.Cancelled,
                    GraphWorkflowFailureClass.Cancelled,
                    "The Development attempt this node run was driving was cancelled.",
                    cancellationToken)
                : await EndImplementAsync(store,
                    graph,
                    run,
                    node,
                    nodeRun,
                    development,
                    task,
                    GraphWorkflowNodeRunStatus.Failed,
                    GraphWorkflowFailures.Classify(FailureClassOf(landed), nodeRun.Attempt, node.MaxAttempts),
                    landed.TerminalReason ?? $"The Development {landed.Role} attempt this node run was driving did not succeed.",
                    cancellationToken);
        }

        if (!await _runtimeSettings.GetDevelopmentEnabledAsync(cancellationToken))
        {
            return await EndImplementAsync(store, graph, run, node, nodeRun, development, task, GraphWorkflowNodeRunStatus.Failed, GraphWorkflowFailureClass.ValidationFailed, DevelopmentOff, cancellationToken);
        }

        return await StartNextActionAsync(store, graph, run, node, nodeRun, development, management, projectId, task, attempts.Count, cancellationToken);
    }

    /// <summary>
    ///     Asks Dev Mode for the next step; the operation id is derived from what this tick observed, so a replayed tick
    ///     asks for the same step and a tick that finds a new state asks for the next.
    /// </summary>
    private async Task<int> StartNextActionAsync(IGraphWorkflowStore store,
        GraphWorkflowGraph graph,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        IDevelopmentStore development,
        IDevelopmentManagementService management,
        Guid projectId,
        DevelopmentTaskSnapshot task,
        int attemptCount,
        CancellationToken cancellationToken)
    {
        var operationId = OperationId(run.Id,
            node.NodeKey,
            nodeRun.Attempt,
            string.Create(CultureInfo.InvariantCulture, $"devtask-{task.Status}-{task.CurrentReviewRound}-{attemptCount}"));
        try
        {
            _ = await management.StartNextActionAsync(projectId, task.Id, operationId, cancellationToken);
            return 1;
        }
        catch (DevelopmentInvalidTransitionException exception)
        {
            // Validation runs with no attempt row, and the Development page drives the same task: a task that moved since this tick's read is working, not stuck.
            var current = await development.GetTaskAsync(task.Id, cancellationToken);
            if (current.Status == DevelopmentTaskStatus.Validation
                || current.Version != task.Version
                || (await development.ListAttemptsAsync(task.Id, cancellationToken)).Any(static attempt => attempt.Status is DevelopmentAttemptStatus.Pending or DevelopmentAttemptStatus.Running))
            {
                return 0;
            }

            return await EndImplementAsync(store, graph, run, node, nodeRun, development, task, GraphWorkflowNodeRunStatus.Failed, GraphWorkflowFailureClass.ValidationFailed, Sanitized(exception), cancellationToken);
        }
        catch (DevelopmentConcurrencyException)
        {
            return 0;
        }
        catch (DevelopmentWorkspaceSecurityException exception)
        {
            // An expired trust acknowledgement or a model this project may not send its repository to: the same refusal next time.
            return await EndImplementAsync(store, graph, run, node, nodeRun, development, task, GraphWorkflowNodeRunStatus.Failed, GraphWorkflowFailureClass.ValidationFailed, Sanitized(exception), cancellationToken);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or KeyNotFoundException))
        {
            // Not surfaced: an unexpected exception's text is unsanitized and can carry a host path.
            _logger.LogError(exception, "Graph workflow run {RunId} DevTask node run {NodeRunId} could not advance its Development task.", run.Id, nodeRun.Id);
            return await EndImplementAsync(store,
                graph,
                run,
                node,
                nodeRun,
                development,
                task,
                GraphWorkflowNodeRunStatus.Failed,
                GraphWorkflowFailures.Classify(GraphWorkflowFailureClass.NodeFailed, nodeRun.Attempt, node.MaxAttempts),
                "This node run's Development task stopped on an unexpected error. The engine log has the detail.",
                cancellationToken);
        }
    }

    /// <summary>Applies the task the named <c>implement</c> node of THIS run pinned, on behalf of this run.</summary>
    /// <remarks>
    ///     The operation id is keyed on the attempt, so a retry asks again while Dev Mode's ledger keeps a replay idempotent;
    ///     a task already applied succeeds without asking. Runs inside the tick: one apply is one ledger write and one git apply.
    /// </remarks>
    private async Task<int> ApplyAsync(IGraphWorkflowStore store,
        GraphWorkflowGraph graph,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        GraphWorkflowDevTaskConfig config,
        IDevelopmentStore development,
        IDevelopmentManagementService management,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var implementKey = config.TaskNodeKey ?? string.Empty;
        var implemented = (await store.ListNodeRunsAsync(run.Id, cancellationToken)).FirstOrDefault(candidate => string.Equals(candidate.NodeKey, implementKey, StringComparison.Ordinal));
        if (implemented is not { Status: GraphWorkflowNodeRunStatus.Succeeded, DevelopmentTaskId: { } taskId })
        {
            // DEVTASK-1 puts the implement node in front of the approval, but a Pause with joinPolicy Any can be reached past it.
            return await FailAsync(store,
                graph,
                run,
                node,
                nodeRun,
                GraphWorkflowFailureClass.ValidationFailed,
                $"Node '{node.NodeKey}' applies the task of '{implementKey}', which has no Development task awaiting apply in this run.",
                cancellationToken);
        }

        var written = nodeRun.Status == GraphWorkflowNodeRunStatus.Pending ? await RunningAsync(store, run, nodeRun, inputJson: null, cancellationToken) : 0;
        nodeRun = nodeRun with
        {
            Status = GraphWorkflowNodeRunStatus.Running
        };

        if ((await development.GetTaskAsync(taskId, cancellationToken)).Status == DevelopmentTaskStatus.Completed)
        {
            return written + await SettleAsync(store, graph, run, node, nodeRun, GraphWorkflowNodeRunStatus.Succeeded, failureClass: null, reason: null, ApplyOutput(taskId, alreadyApplied: true), cancellationToken);
        }

        string refusal;
        GraphWorkflowFailureClass failureClass;
        try
        {
            var result = await management.ApplyAsync(projectId,
                taskId,
                OperationId(run.Id, node.NodeKey, nodeRun.Attempt, $"apply-{taskId:N}"),
                new DevelopmentWorkflowRunReference
                {
                    Engine = DevelopmentWorkflowEngine.GraphWorkflow,
                    RunId = run.Id
                },
                cancellationToken);
            if (!string.Equals(result.Phase, DevelopmentOperationPhases.ApplyBlocked, StringComparison.Ordinal))
            {
                return written + await SettleAsync(store, graph, run, node, nodeRun, GraphWorkflowNodeRunStatus.Succeeded, failureClass: null, reason: null, ApplyOutput(taskId, alreadyApplied: false), cancellationToken);
            }

            refusal = "The Development apply gate declined the patch: the repository is not at the exact base the approved patch was reviewed against.";
            failureClass = GraphWorkflowFailureClass.ValidationFailed;
        }
        catch (Exception exception) when (exception is DevelopmentInvalidTransitionException or DevelopmentWorkspaceSecurityException)
        {
            // The ownership guard, the evidence chain or the repository refused; asking again answers none of those.
            refusal = Sanitized(exception);
            failureClass = GraphWorkflowFailureClass.ValidationFailed;
        }
        catch (DevelopmentConcurrencyException exception)
        {
            // Another writer on the project's ledger: a retry finds the task applied or applies it.
            refusal = Sanitized(exception);
            failureClass = GraphWorkflowFailures.Classify(GraphWorkflowFailureClass.NodeFailed, nodeRun.Attempt, node.MaxAttempts);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or KeyNotFoundException))
        {
            // Escaping would leave the row Running and re-apply on every poll; the text is unsanitized, so it stays in the log.
            _logger.LogError(exception, "Graph workflow run {RunId} DevTask node run {NodeRunId} could not apply its Development task.", run.Id, nodeRun.Id);
            refusal = "This node run's apply stopped on an unexpected error. The engine log has the detail.";
            failureClass = GraphWorkflowFailures.Classify(GraphWorkflowFailureClass.NodeFailed, nodeRun.Attempt, node.MaxAttempts);
        }

        return written + await SettleAsync(store, graph, run, node, nodeRun, GraphWorkflowNodeRunStatus.Failed, failureClass, refusal, ApplyOutput(taskId, alreadyApplied: false), cancellationToken);
    }

    /// <summary>The task this implement node creates, keyed on run and node key so every attempt and every replay finds the same one.</summary>
    /// <remarks>Acceptance criteria and the review budget fall back to the project's first task, which carries the project's standard of done.</remarks>
    private static async Task<Guid> CreateTaskAsync(IDevelopmentStore development,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraphNode node,
        GraphWorkflowDevTaskConfig config,
        Guid projectId,
        string requirements,
        CancellationToken cancellationToken)
    {
        var tasks = await development.ListTasksAsync(projectId, cancellationToken);
        var first = tasks.Count > 0 ? tasks[0] : null;
        var created = await development.CreateTaskAsync(new DevelopmentCreateTaskCommand
            {
                ProjectId = projectId,
                TaskId = Guid.NewGuid(),
                OperationId = OperationId(run.Id, node.NodeKey, attempt: 0, "devtask-create"),
                Title = string.IsNullOrWhiteSpace(config.Title) ? node.Label : config.Title,
                Requirements = requirements,
                AcceptanceCriteriaJson = config.AcceptanceCriteria.Count > 0
                    ? JsonSerializer.Serialize(config.AcceptanceCriteria, GraphWorkflowInvocationExecutor.PromptJsonOptions)
                    : first?.AcceptanceCriteriaJson ?? "[]",
                MaxReviewRounds = first?.MaxReviewRounds ?? DefaultMaxReviewRounds
            },
            cancellationToken);
        return created.TaskId ?? throw new InvalidOperationException("The Development store created a task without naming it.");
    }

    /// <summary>Pins the task on the row; false when the run stopped being live first, whether the store declined or the cancel's write beat this one.</summary>
    /// <remarks>The tick's context tracks the run it already read, so a cancel committed in between surfaces as a lost token race, not a decline.</remarks>
    private static async Task<bool> PinAsync(IGraphWorkflowStore store, GraphWorkflowRunSnapshot run, GraphWorkflowNodeRunSnapshot nodeRun, Guid taskId, CancellationToken cancellationToken)
    {
        try
        {
            return await store.SetNodeRunDevelopmentTaskAsync(run.Id, nodeRun.Id, taskId, cancellationToken) is GraphWorkflowConditionalWriteResult.Written;
        }
        catch (GraphWorkflowInvalidTransitionException)
        {
            if ((await store.GetRunAsync(run.Id, cancellationToken)).Status is GraphWorkflowRunStatus.Running or GraphWorkflowRunStatus.WaitingForApproval)
            {
                // Still live: another writer only moved the run, so the task stays for the keyed re-create of a later attempt.
                throw;
            }

            return false;
        }
    }

    /// <summary>Cancels a task this lane created but could not pin because the run ended in between; nothing was started on it.</summary>
    private static async Task CancelOrphanAsync(IDevelopmentStore development, GraphWorkflowRunSnapshot run, GraphWorkflowGraphNode node, Guid taskId, CancellationToken cancellationToken)
    {
        var task = await development.GetTaskAsync(taskId, cancellationToken);
        try
        {
            _ = await development.TransitionTaskAsync(new DevelopmentTransitionTaskCommand
                {
                    TaskId = taskId,
                    OperationId = OperationId(run.Id, node.NodeKey, attempt: 0, "devtask-orphan-cancel"),
                    TargetStatus = DevelopmentTaskStatus.Cancelled,
                    ExpectedTaskVersion = task.Version,
                    Reason = $"Graph workflow run '{run.Id:D}' ended before node '{node.NodeKey}' could start this task."
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is DevelopmentConcurrencyException or DevelopmentInvalidTransitionException)
        {
            // Something else moved the task first (an operator, or an earlier tick's cancel); it is no longer a silent Planned orphan.
        }
    }

    /// <summary>Ends an implement row, revoking its instructions first so they do not govern the operator's later rounds.</summary>
    private async Task<int> EndImplementAsync(IGraphWorkflowStore store,
        GraphWorkflowGraph graph,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        IDevelopmentStore development,
        DevelopmentTaskSnapshot task,
        GraphWorkflowNodeRunStatus target,
        GraphWorkflowFailureClass? failureClass,
        string? reason,
        CancellationToken cancellationToken)
    {
        await RecordInstructionsAsync(development, run, node, nodeRun.Attempt, task.Id, instructions: null, cancellationToken);
        return await SettleAsync(store,
            graph,
            run,
            node,
            nodeRun,
            target,
            failureClass,
            reason,
            new ImplementOutput
            {
                DevelopmentTaskId = task.Id,
                TaskStatus = task.Status.ToString(),
                ReviewRound = task.CurrentReviewRound
            },
            cancellationToken);
    }

    /// <summary>Cancels the attempt the row's task has in flight and revokes the row's instructions; the task itself survives.</summary>
    private static async Task StandDownAsync(IDevelopmentStore development,
        IDevelopmentManagementService management,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        await RecordInstructionsAsync(development, run, node, nodeRun.Attempt, taskId, instructions: null, cancellationToken);
        if ((await development.ListAttemptsAsync(taskId, cancellationToken))
            .LastOrDefault(static attempt => attempt.Status is DevelopmentAttemptStatus.Pending or DevelopmentAttemptStatus.Running) is { } active)
        {
            _ = await management.CancelAttemptAsync(projectId, taskId, active.Id, cancellationToken);
        }
    }

    /// <summary>
    ///     Puts the node's <c>instructions</c> where Dev Mode's coder and reviewer prompts read policy, named as one rule set; null
    ///     or blank records the clear. Keyed on attempt and phase, so a replayed dispatch appends nothing.
    /// </summary>
    private static async Task RecordInstructionsAsync(IDevelopmentStore development,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraphNode node,
        int attempt,
        Guid taskId,
        string? instructions,
        CancellationToken cancellationToken)
    {
        var text = instructions?.Trim() ?? string.Empty;
        IReadOnlyList<DevelopmentWorkflowRuleSetReference> named = text.Length == 0
            ? []
            :
            [
                new DevelopmentWorkflowRuleSetReference(OperationId(run.Id, node.NodeKey, attempt: 0, "devtask-instructions"),
                    $"Graph workflow node '{node.NodeKey}' instructions",
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))))
            ];
        _ = await development.RecordWorkflowPolicyAsync(taskId,
            OperationId(run.Id, node.NodeKey, attempt, text.Length == 0 ? "devtask-policy-clear" : "devtask-policy"),
            text,
            named,
            cancellationToken);
    }

    /// <summary>A workspace policy refusal is the engine declining on evidence; an interrupted attempt is a restart; the rest is the coder failing.</summary>
    private static GraphWorkflowFailureClass FailureClassOf(DevelopmentAttemptSnapshot attempt)
    {
        if (attempt.Status == DevelopmentAttemptStatus.Interrupted)
        {
            return GraphWorkflowFailureClass.Interrupted;
        }

        return DevelopmentAttemptEvidenceException.Names(attempt.TerminalReason, DevelopmentAttemptFailureCodes.WorkspacePolicyRefused)
            ? GraphWorkflowFailureClass.ValidationFailed
            : GraphWorkflowFailureClass.NodeFailed;
    }

    /// <summary>The run's Development project, read from its stored start input as the start binding and the Agent node read it.</summary>
    private static Guid? ProjectId(GraphWorkflowRunSnapshot run) =>
        GraphWorkflowDocuments.Resolve(run.InputJson, GraphWorkflowInvocationExecutor.DevelopmentProjectIdKey) is { ValueKind: JsonValueKind.String } value
        && value.TryGetGuid(out var projectId)
            ? projectId
            : null;

    /// <summary>A deterministic Dev Mode operation id per run, node key, attempt and phase, so Dev Mode's ledger makes a replay a no-op.</summary>
    private static Guid OperationId(Guid runId, string nodeKey, int attempt, string phase)
    {
        var material = string.Create(CultureInfo.InvariantCulture, $"graph-workflow:{runId:N}:{nodeKey}:{attempt}:{phase}");
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(material)).AsSpan(start: 0, length: 16));
    }

    /// <summary>An exception's message fit for a row; one the sanitizer refuses is replaced, never repeated.</summary>
    private static string Sanitized(Exception exception)
    {
        try
        {
            return DevelopmentArtifactSanitizer.SanitizeText(exception.Message);
        }
        catch (DevelopmentWorkspaceSecurityException)
        {
            return "Development refused this node run, and the reason could not be shown safely. The engine log has the detail.";
        }
    }

    private static ApplyOutputDocument ApplyOutput(Guid taskId, bool alreadyApplied) =>
        new()
        {
            DevelopmentTaskId = taskId,
            AlreadyApplied = alreadyApplied
        };

    /// <summary>The input document this row reads, over the satisfied inbound edges the admission judged.</summary>
    private static async Task<string> InputDocumentAsync(IGraphWorkflowStore store,
        GraphWorkflowGraph graph,
        GraphWorkflowGraphNode node,
        GraphWorkflowRunSnapshot run,
        CancellationToken cancellationToken)
    {
        var byKey = (await store.ListNodeRunsAsync(run.Id, cancellationToken)).ToDictionary(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal);
        return GraphWorkflowDocuments.ComposeInput(run, GraphWorkflowInlineExecutor.Upstream(graph, node, byKey));
    }

    private static async Task<int> RunningAsync(IGraphWorkflowStore store,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowNodeRunSnapshot nodeRun,
        string? inputJson,
        CancellationToken cancellationToken)
    {
        GraphWorkflowStateMachine.EnsureLegal(nodeRun.Status, GraphWorkflowNodeRunStatus.Running, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand
            {
                RunId = run.Id,
                NodeRunId = nodeRun.Id,
                ExpectedVersion = GraphWorkflowVersions.Any,
                TargetStatus = GraphWorkflowNodeRunStatus.Running,
                InputJson = inputJson
            },
            cancellationToken);
        return 1;
    }

    private Task<int> FailAsync(IGraphWorkflowStore store,
        GraphWorkflowGraph graph,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        GraphWorkflowFailureClass failureClass,
        string reason,
        CancellationToken cancellationToken) =>
        SettleAsync(store, graph, run, node, nodeRun, GraphWorkflowNodeRunStatus.Failed, failureClass, reason, output: null, cancellationToken);

    /// <summary>Writes the row's terminal, walking a <c>Pending</c> row through <c>Running</c> first: a failure needs an opened attempt.</summary>
    private async Task<int> SettleAsync(IGraphWorkflowStore store,
        GraphWorkflowGraph graph,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        GraphWorkflowNodeRunStatus target,
        GraphWorkflowFailureClass? failureClass,
        string? reason,
        object? output,
        CancellationToken cancellationToken)
    {
        var written = 0;
        if (nodeRun.Status == GraphWorkflowNodeRunStatus.Pending && target != GraphWorkflowNodeRunStatus.Cancelled)
        {
            written += await RunningAsync(store, run, nodeRun, inputJson: null, cancellationToken);
            nodeRun = nodeRun with
            {
                Status = GraphWorkflowNodeRunStatus.Running
            };
        }

        GraphWorkflowStateMachine.EnsureLegal(nodeRun.Status, target, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand
            {
                RunId = run.Id,
                NodeRunId = nodeRun.Id,
                ExpectedVersion = GraphWorkflowVersions.Any,
                TargetStatus = target,
                OutputJson = target == GraphWorkflowNodeRunStatus.Cancelled ? null : Document(graph, node, nodeRun.Attempt, target, output),
                FailureClass = failureClass,
                TerminalReason = reason is null ? null : GraphWorkflowStateMachine.Bounded(reason, GraphWorkflowStateMachine.MaxTerminalReason)
            },
            cancellationToken);
        return written + 1;
    }

    /// <summary>The envelope over <paramref name="output" />, or null when even that is over the cap: the row still settles and says why.</summary>
    private string? Document(GraphWorkflowGraph graph, GraphWorkflowGraphNode node, int attempt, GraphWorkflowNodeRunStatus target, object? output)
    {
        try
        {
            return GraphWorkflowDocuments.Compose(graph,
                node,
                attempt,
                target == GraphWorkflowNodeRunStatus.Succeeded ? GraphWorkflowNodeOutputStatuses.Succeeded : GraphWorkflowNodeOutputStatuses.Failed,
                output is null ? GraphWorkflowDocuments.EmptyObject : JsonSerializer.SerializeToElement(output, output.GetType(), JsonOptions),
                _options.MaxOutputJsonBytes);
        }
        catch (GraphWorkflowOutputTooLargeException)
        {
            return null;
        }
    }

    /// <summary>What an <c>implement</c> node answers: the task a reader drills into, and where Dev Mode left it.</summary>
    private sealed record ImplementOutput
    {
        public required Guid DevelopmentTaskId { get; init; }

        public required string TaskStatus { get; init; }

        public required int ReviewRound { get; init; }
    }

    /// <summary>What an <c>apply</c> node answers: the task it applied, and whether it found it applied already.</summary>
    private sealed record ApplyOutputDocument
    {
        public required Guid DevelopmentTaskId { get; init; }

        public required bool AlreadyApplied { get; init; }
    }
}
