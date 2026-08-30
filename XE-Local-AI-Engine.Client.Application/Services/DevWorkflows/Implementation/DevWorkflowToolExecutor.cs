namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The sandbox lane: a bounded number of Tool node-runs may hold a prepared workspace at once, each driven by a
///     detached task that produces a result and never writes a row.
///     <para>
///         A singleton, and that is the whole reason this is a class of its own rather than a method on the dispatcher:
///         the slot count and the in-flight registry outlive a tick and a scope, and a second instance would hand out
///         the same slots twice. The dispatcher resolves it once and asks it three questions — dispatch this, has this
///         landed, stop this — exactly as it asks the agent executor.
///     </para>
///     <para>
///         The slot is taken BEFORE the row moves to <c>Running</c> and released in the detached task's <c>finally</c>,
///         which is the shape the work-session admission uses, so the cap holds across concurrent admissions rather
///         than merely looking like it does.
///     </para>
/// </summary>
internal sealed class DevWorkflowToolExecutor : IAsyncDisposable
{
    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     The two classes a retry cannot help. They stand the node run down for a human rather than failing it, because
    ///     the run's other branches may still be worth finishing and a person has to change something either way.
    /// </summary>
    private static readonly HashSet<string> HumanOnlyFailureClasses = new(StringComparer.Ordinal)
    {
        DevWorkflowFailureClasses.Configuration,
        DevWorkflowFailureClasses.Policy
    };

    private readonly IDevWorkflowArtifactBlobStore _blobs;
    private readonly ConcurrentDictionary<Guid, InFlight> _inflight = new();
    private readonly SemaphoreSlim _lane;
    private readonly ILogger<DevWorkflowToolExecutor> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    public DevWorkflowToolExecutor(IServiceScopeFactory scopeFactory,
        IDevWorkflowArtifactBlobStore blobs,
        IOptions<DevWorkflowOptions> options,
        ILogger<DevWorkflowToolExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lane = new SemaphoreSlim(options.Value.MaxParallelToolNodes, options.Value.MaxParallelToolNodes);
    }

    /// <summary>Whether this node run's commands are being driven right now, or have landed and not yet been read.</summary>
    public bool IsInFlight(Guid nodeRunId) =>
        _inflight.ContainsKey(nodeRunId);

    /// <summary>
    ///     Admits an eligible tool node-run, and answers how many transitions it wrote.
    ///     <para>
    ///         The row goes to <c>Queued</c> first even when a slot is free a line later, for the same reason the agent
    ///         lane does it: three validation nodes on a two-slot lane are <c>Running, Running, Queued</c>, and a reader
    ///         has to be able to see that rather than infer it from timing.
    ///     </para>
    /// </summary>
    public async Task<int> DispatchAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowGraphNode node,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(nodeRun);

        if (_inflight.ContainsKey(nodeRun.Id))
        {
            // The commands are already running: the only thing that can have left the row behind them is the Running
            // write below having failed. Re-running them would spend a whole build to arrive at the answer already in
            // hand, so the row is caught up instead and the next poll settles it.
            if (nodeRun.Status == DevWorkflowNodeRunStatus.Running)
            {
                return 0;
            }

            return await RunningAsync(store, run, nodeRun, cancellationToken).ConfigureAwait(false);
        }

        var written = 0;
        if (nodeRun.Status == DevWorkflowNodeRunStatus.Pending)
        {
            DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Queued, nodeRun.NodeKey);
            _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                                   nodeRun.Id,
                                   DevWorkflowVersions.Any,
                                   DevWorkflowNodeRunStatus.Queued,
                                   QueueReason: DevWorkflowQueueReasons.AwaitingSandboxSlot),
                               cancellationToken)
                           .ConfigureAwait(false);
            written++;
        }

        if (!await _lane.WaitAsync(millisecondsTimeout: 0, cancellationToken).ConfigureAwait(false))
        {
            // Queueing, not failure: the lane is simply full. No event and no failure class — the row's reason says
            // what it is waiting for, and the next tick asks again.
            return written;
        }

        // Started before the row says Running, and the slot is already held: the task releases it in its own finally,
        // so a throw anywhere below cannot leak the slot.
        _ = _inflight.TryAdd(nodeRun.Id, Start(run, node, nodeRun));
        return written + await RunningAsync(store, run, nodeRun, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads what the node run's commands came to and settles the row when they have landed, answering how many
    ///     transitions it wrote.
    /// </summary>
    public async Task<int> PollAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(nodeRun);

        if (!_inflight.TryGetValue(nodeRun.Id, out var flight))
        {
            // Nothing on this node is driving this row, and nothing ever will: the lane holds no memory across a
            // restart, which is precisely why the startup reconciler collapses such rows before the dispatcher runs.
            // Reaching here means it did not, so the row is settled for what it is rather than swept forever.
            return await SettleAsync(store,
                    run,
                    nodeRun,
                    nodeRuns,
                    DevWorkflowNodeRunStatus.Failed,
                    DevWorkflowFailureClasses.Interrupted,
                    "The host stopped while this node run was running its validation commands.",
                    Output(nodeRun, DevWorkflowFailureClasses.Interrupted, run: null),
                    DevWorkflowOutcomes.Interrupted,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!flight.Work.IsCompleted)
        {
            // Still building. The dispatcher holds nothing about it, so this is the only place its state is read.
            return 0;
        }

        _ = _inflight.TryRemove(nodeRun.Id, out _);
        flight.Cancellation.Dispose();

        if (flight.Work.IsCanceled)
        {
            return await SettleAsync(store,
                    run,
                    nodeRun,
                    nodeRuns,
                    DevWorkflowNodeRunStatus.Cancelled,
                    DevWorkflowFailureClasses.Cancelled,
                    "The run was cancelled while this node run was running its validation commands.",
                    Output(nodeRun, DevWorkflowFailureClasses.Cancelled, run: null),
                    DevWorkflowOutcomes.Cancelled,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var result = await flight.Work.ConfigureAwait(false);
        await RecordSecretsAsync(store, run, nodeRun, result, cancellationToken).ConfigureAwait(false);

        // Evidence first, status last — the same order the agent lane and the work-session loop use one level down. A
        // crash in that window re-derives the same answer, because the artifact write is keyed and the poll runs again.
        await PromoteReportAsync(store, run, nodeRun, result, cancellationToken).ConfigureAwait(false);

        if (result.Passed)
        {
            return await SettleAsync(store,
                    run,
                    nodeRun,
                    nodeRuns,
                    DevWorkflowNodeRunStatus.Succeeded,
                    failureClass: null,
                    terminalReason: null,
                    Output(nodeRun, failureClass: null, result),
                    outcome: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var failureClass = result.FailureClass ?? DevWorkflowFailureClasses.Internal;
        var target = HumanOnlyFailureClasses.Contains(failureClass) ? DevWorkflowNodeRunStatus.Blocked : DevWorkflowNodeRunStatus.Failed;
        return await SettleAsync(store,
                run,
                nodeRun,
                nodeRuns,
                target,
                failureClass,
                result.SanitizedReason ?? "This node run's validation commands did not pass.",
                Output(nodeRun, failureClass, result),
                failureClass == DevWorkflowFailureClasses.Timeout ? DevWorkflowOutcomes.Timeout : null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Asks a node run's commands to stop. Answers whether there was anything to ask.
    ///     <para>
    ///         The row is deliberately NOT settled here: a build asked to stop is still winding down, and only the next
    ///         tick's poll knows whether it landed cancelled or finished inside the window. Settling it here would also
    ///         hold the advance gate — and with it every other run — for as long as the stop took.
    ///     </para>
    /// </summary>
    public async Task<bool> StopAsync(Guid nodeRunId)
    {
        if (!_inflight.TryGetValue(nodeRunId, out var flight))
        {
            return false;
        }

        await flight.Cancellation.CancelAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    ///     Waits for one node run's commands to land, so a test that provisions a real sandbox can drive ticks rather
    ///     than sleep between them. Returns immediately when nothing is in flight.
    /// </summary>
    internal Task WaitForCompletionAsync(Guid nodeRunId) =>
        _inflight.TryGetValue(nodeRunId, out var flight) ? SwallowAsync(flight.Work) : Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, value: 1) == 1)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var flight in _inflight.Values)
        {
            await SwallowAsync(flight.Work).ConfigureAwait(false);
            flight.Cancellation.Dispose();
        }

        _inflight.Clear();
        _shutdown.Dispose();
        _lane.Dispose();
    }

    /// <summary>The detached pass: its own scope, its own cancellation, and a result rather than a row.</summary>
    [SuppressMessage("Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership transfers to the in-flight entry, which outlives this call by design: the poll disposes it "
                        + "when it settles the row, and DisposeAsync disposes whatever is left. Disposing here would cancel "
                        + "the pass that was just started.")]
    private InFlight Start(DevWorkflowRunSnapshot run, DevWorkflowGraphNode node, DevWorkflowNodeRunSnapshot nodeRun)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        return new InFlight(cancellation, RunAsync(run, node, nodeRun, cancellation.Token));
    }

    private async Task<DevWorkflowToolRun> RunAsync(DevWorkflowRunSnapshot run,
        DevWorkflowGraphNode node,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<IDevWorkflowToolCommands>() is not { } commands)
            {
                // Development Mode is switched off on this node, so there is no workspace provider, no repository
                // binding and no sandbox. Nothing here can run, and no retry changes that.
                return Refused(DevWorkflowFailureClasses.Configuration,
                    "This node runs repository commands, and Development Mode is switched off on this node.");
            }

            return await commands.RunAsync(run, node, nodeRun, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Faulting the task on purpose: the poll reads cancellation off the task rather than off a flag, so a stop
            // that landed and a stop that arrived too late are told apart by the same mechanism.
            throw;
        }
        catch (Exception exception)
        {
            // The message is NOT surfaced: an unexpected exception's text is the one string in this lane nothing has
            // sanitized, and it can carry a host path or a fragment of captured output.
            _logger.LogError(exception, "Development workflow tool node run {NodeRunId} of run {RunId} failed unexpectedly.", nodeRun.Id, run.Id);
            return Refused(DevWorkflowFailureClasses.Internal,
                "This node run's validation commands stopped on an unexpected error. The engine log has the detail.");
        }
        finally
        {
            _ = _lane.Release();
        }
    }

    private static DevWorkflowToolRun Refused(string failureClass, string sanitizedReason) =>
        new(Passed: false,
            failureClass,
            FailureCode: null,
            sanitizedReason,
            CommandsRun: 0,
            CommandsFailed: 0,
            TestsPassed: null,
            TestsFailed: null,
            ReadOnlyMemory<byte>.Empty,
            []);

    private static async Task<int> RunningAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Running, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               DevWorkflowVersions.Any,
                               DevWorkflowNodeRunStatus.Running),
                           cancellationToken)
                       .ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    ///     Records the committed credentials the prepared workspace carried. The paths are the workspace's own tracked
    ///     file names, which is what makes them safe to name and what makes naming them worth doing.
    /// </summary>
    private static async Task RecordSecretsAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        DevWorkflowToolRun result,
        CancellationToken cancellationToken)
    {
        if (result.SecretPaths.Count == 0)
        {
            return;
        }

        _ = await store.AppendEventAsync(new AppendDevWorkflowEventCommand(run.Id,
                               DevWorkflowVersions.Any,
                               DevWorkflowEventTypes.WorkspaceSecretsDetected,
                               nodeRun.Id,
                               DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "workspace-secrets"),
                               DetailJson: JsonSerializer.Serialize(new SecretsDetail(result.SecretPaths), JsonOptions)),
                           cancellationToken)
                       .ConfigureAwait(false);
    }

    /// <summary>
    ///     Writes the validation report into the run's own artifact record, so the evidence outlives the workspace it
    ///     was produced in. Keyed on <c>(run, node key, attempt)</c>, so a replayed poll rewrites the same blob and the
    ///     store's query-first check returns the recorded result instead of appending a second version.
    /// </summary>
    private async Task PromoteReportAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        DevWorkflowToolRun result,
        CancellationToken cancellationToken)
    {
        if (result.Report.IsEmpty)
        {
            // A pass refused before any command ran has nothing to report; its reason is on the row.
            return;
        }

        var artifactId = DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "validation-report");
        var write = await _blobs.WriteAsync(run.Id, artifactId, result.Report, cancellationToken).ConfigureAwait(false);
        var appended = await store.AppendArtifactAsync(new AppendDevWorkflowArtifactCommand(run.Id,
                                          artifactId,
                                          nodeRun.Id,
                                          DevWorkflowVersions.Any,
                                          DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "report"),
                                          DevWorkflowArtifactKind.ValidationReport,
                                          $"{nodeRun.NodeKey}-validation.json",
                                          "application/json",
                                          write.ContentHash,
                                          write.ByteCount,
                                          write.OpaqueReference),
                                      cancellationToken)
                                  .ConfigureAwait(false);

        if (appended.SupersededArtifactId is not { } superseded)
        {
            return;
        }

        // A re-attempt's report replaces the one a downstream node may already have read. Mark-only: nothing is
        // regenerated, and a human decides what a stale consumer is worth.
        _ = await store.MarkDependentsStaleAsync(new MarkDevWorkflowStaleCommand(run.Id,
                               superseded,
                               artifactId,
                               DevWorkflowVersions.Any,
                               DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "report-stale")),
                           cancellationToken)
                       .ConfigureAwait(false);
    }

    private static async Task<int> SettleAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        DevWorkflowNodeRunStatus target,
        string? failureClass,
        string? terminalReason,
        string outputJson,
        string? outcome,
        CancellationToken cancellationToken)
    {
        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, target, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               DevWorkflowVersions.Any,

                               // A node run standing down for a human names the answer it is waiting for, the same way
                               // every other blocked row does.
                               target,
                               PendingDecisionKind: target == DevWorkflowNodeRunStatus.Blocked ? DevWorkflowDecisionKind.Abandon : null,
                               OutputJson: outputJson,
                               FailureClass: failureClass,
                               TerminalReason: terminalReason,
                               Outcome: outcome,
                               WorkItemStatus: target == DevWorkflowNodeRunStatus.Blocked
                                   ? DevWorkflowWorkItemStatus.Blocked
                                   : DevWorkflowStateMachine.WorkItemStatusAfter(run.Status, nodeRuns, nodeRun.Id, target)),
                           cancellationToken)
                       .ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    ///     The tool node's slice of the output document every executor writes: the verdict a conditional edge routes on,
    ///     and the counts a fix-loop objective quotes. No command text — an output document is routing data, and the
    ///     evidence lives in the report artifact.
    /// </summary>
    private static string Output(DevWorkflowNodeRunSnapshot nodeRun, string? failureClass, DevWorkflowToolRun? run) =>
        JsonSerializer.Serialize(new ToolOutput(run is { Passed: true } ? DevWorkflowNodeOutputStatuses.Succeeded : DevWorkflowNodeOutputStatuses.Failed,
                nodeRun.Attempt,
                failureClass,
                run?.Passed ?? false,
                run?.FailureCode,
                run?.CommandsRun ?? 0,
                run?.CommandsFailed ?? 0,
                run?.TestsPassed,
                run?.TestsFailed),
            JsonOptions);

    /// <summary>Awaits a detached pass without letting its outcome escape; the poll is what reads that.</summary>
    private static async Task SwallowAsync(Task work)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Every outcome is the poll's to read off the task, including this one.
        }
    }

    private sealed record InFlight(CancellationTokenSource Cancellation, Task<DevWorkflowToolRun> Work);

    private sealed record SecretsDetail(IReadOnlyList<string> Paths);

    private sealed record ToolOutput(
        string Status,
        int Attempt,
        string? FailureClass,
        bool Passed,
        string? FailureCode,
        int CommandsRun,
        int CommandsFailed,
        int? TestsPassed,
        int? TestsFailed);
}
