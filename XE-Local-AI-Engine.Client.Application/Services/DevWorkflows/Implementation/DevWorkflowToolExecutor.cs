namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>The sandbox lane: a bounded number of Tool node-runs may hold a prepared workspace at once.</summary>
/// <remarks>
///     Each is driven by a detached task that produces a result and never writes a row. A SINGLETON, which is why
///     this is a class of its own rather than a method on the dispatcher: the slot count and the in-flight registry
///     outlive a tick and a scope. See docs/wiki/25-dev-workflows.md ("The tool lane").
/// </remarks>
internal sealed class DevWorkflowToolExecutor : IAsyncDisposable
{
    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDevWorkflowArtifactBlobStore _blobs;
    private readonly ConcurrentDictionary<Guid, InFlight> _inflight = new();
    private readonly SemaphoreSlim _lane;
    private readonly ILogger<DevWorkflowToolExecutor> _logger;
    private readonly DevWorkflowRetryPolicy _retries;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    public DevWorkflowToolExecutor(IServiceScopeFactory scopeFactory,
        IDevWorkflowArtifactBlobStore blobs,
        DevWorkflowRetryPolicy retries,
        IOptions<DevWorkflowOptions> options,
        ILogger<DevWorkflowToolExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
        _retries = retries ?? throw new ArgumentNullException(nameof(retries));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lane = new SemaphoreSlim(options.Value.MaxParallelToolNodes, options.Value.MaxParallelToolNodes);
    }

    /// <summary>Whether this node run's commands are being driven right now, or have landed and not yet been read.</summary>
    public bool IsInFlight(Guid nodeRunId) =>
        _inflight.ContainsKey(nodeRunId);

    /// <summary>Admits an eligible tool node-run, and answers how many transitions it wrote.</summary>
    /// <remarks>
    ///     The row goes to <c>Queued</c> first even when a slot is free a line later, for the reason the agent lane
    ///     does it. See docs/wiki/25-dev-workflows.md ("The tool lane").
    /// </remarks>
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

        if (_inflight.TryGetValue(nodeRun.Id, out var inflight) && inflight.Attempt == nodeRun.Attempt)
        {
            // THIS attempt's commands are already running, the Running write below having failed, so the row is caught
            // up rather than re-run. The ATTEMPT is compared, not assumed: a fix loop can reset a row this lane drives.
            return nodeRun.Status == DevWorkflowNodeRunStatus.Running
                ? 0
                : await RunningAsync(store, run, nodeRun, cancellationToken);
        }

        var written = 0;
        if (nodeRun.Status == DevWorkflowNodeRunStatus.Pending)
        {
            DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Queued, nodeRun.NodeKey);
            _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
            {
                RunId = run.Id,
                NodeRunId = nodeRun.Id,
                ExpectedVersion = DevWorkflowVersions.Any,
                TargetStatus = DevWorkflowNodeRunStatus.Queued,
                QueueReason = DevWorkflowQueueReasons.AwaitingSandboxSlot
            },
                               cancellationToken);
            written++;
        }

        if (!await _lane.WaitAsync(millisecondsTimeout: 0, cancellationToken))
        {
            // Queueing, not failure: the lane is simply full. No event and no failure class — the row's reason says
            // what it is waiting for, and the next tick asks again.
            return written;
        }

        // Started before the row says Running, and the slot is already held: the task releases it in its own finally,
        // so a throw anywhere below cannot leak the slot.
        _ = _inflight.TryAdd(nodeRun.Id, Start(run, node, nodeRun));
        return written + await RunningAsync(store, run, nodeRun, cancellationToken);
    }

    /// <summary>
    ///     Reads what the node run's commands came to and settles the row when they have landed, answering how many
    ///     transitions it wrote.
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

        if (!_inflight.TryGetValue(nodeRun.Id, out var flight))
        {
            // Nothing is driving this row and nothing ever will: the lane holds no memory across a restart, and the
            // startup reconciler did not collapse it. Judged here rather than swept forever; an interrupted pass retries.
            return await _retries.SettleFailureAsync(store,
                                     graph,
                                     run,
                                     nodeRun,
                                     nodeRuns,
                                     new DevWorkflowFailure
                                     {
                                         FailureClass = DevWorkflowFailureClasses.Interrupted,
                                         SanitizedReason = StoppedReason(graph, nodeRun, "The host stopped"),
                                         OutputJson = Output(nodeRun, DevWorkflowFailureClasses.Interrupted, run: null),
                                         Outcome = DevWorkflowOutcomes.Interrupted
                                     },
                                     cancellationToken);
        }

        var written = 0;
        if (nodeRun.Status == DevWorkflowNodeRunStatus.Queued)
        {
            // The row never caught up with its pass: the Running write failed after the slot and entry were taken.
            // Outside a drain the next admission repairs it; inside one nothing admits, so the poll has to.
            written = await RunningAsync(store, run, nodeRun, cancellationToken);
            nodeRun = nodeRun with
            {
                Status = DevWorkflowNodeRunStatus.Running
            };
        }

        if (!flight.Work.IsCompleted)
        {
            // Still building. The dispatcher holds nothing about it, so this is the only place its state is read.
            return written;
        }

        written += flight.Work.IsCanceled
            ? await SettleAsync(store,
                    run,
                    nodeRun,
                    nodeRuns,
                    DevWorkflowNodeRunStatus.Cancelled,
                    DevWorkflowFailureClasses.Cancelled,
                    StoppedReason(graph, nodeRun, "The run was cancelled"),
                    Output(nodeRun, DevWorkflowFailureClasses.Cancelled, run: null),
                    DevWorkflowOutcomes.Cancelled,
                    cancellationToken)
            : await SettleLandedAsync(store, graph, run, nodeRun, nodeRuns, await flight.Work, cancellationToken);

        // Consumed only once the settle has COMMITTED: doing it first would spend the result on a write that may
        // throw, and the next poll would report "the host stopped" about a pass that finished perfectly.
        _ = _inflight.TryRemove(nodeRun.Id, out _);
        flight.Cancellation.Dispose();
        return written;
    }

    /// <summary>Turns one landed pass into evidence and a status. Idempotent: every write it makes is keyed.</summary>
    private async Task<int> SettleLandedAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        DevWorkflowToolRun result,
        CancellationToken cancellationToken)
    {
        await RecordSecretsAsync(store, run, nodeRun, result, cancellationToken);

        // Evidence first, status last — the same order the agent lane and the work-session loop use one level down. A
        // crash in that window re-derives the same answer, because the artifact write is keyed and the poll runs again.
        await PromoteReportAsync(store, graph, run, nodeRun, result, cancellationToken);

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
                    cancellationToken);
        }

        if (string.Equals(result.FailureClass, DevWorkflowFailureClasses.Cancelled, StringComparison.Ordinal))
        {
            // A pass ASKED to stop, which answered with an account of what it had done rather than letting the token
            // throw. Same terminal as the cancelled arm; it comes through here only so the evidence comes with it.
            return await SettleAsync(store,
                    run,
                    nodeRun,
                    nodeRuns,
                    DevWorkflowNodeRunStatus.Cancelled,
                    DevWorkflowFailureClasses.Cancelled,
                    result.SanitizedReason ?? "The run was cancelled while this node run was working.",
                    Output(nodeRun, DevWorkflowFailureClasses.Cancelled, result),
                    DevWorkflowOutcomes.Cancelled,
                    cancellationToken);
        }

        // A failed verdict is the fix loop's fuel rather than an error, so where it goes — another attempt here, another
        // attempt at the node that produced what these commands judged, or a human — is the retry policy's to decide.
        var failureClass = result.FailureClass ?? DevWorkflowFailureClasses.Internal;
        return await _retries.SettleFailureAsync(store,
                                 graph,
                                 run,
                                 nodeRun,
                                 nodeRuns,
                                 new DevWorkflowFailure
                                 {
                                     FailureClass = failureClass,
                                     SanitizedReason = result.SanitizedReason ?? "This node run's validation commands did not pass.",
                                     OutputJson = Output(nodeRun, failureClass, result),
                                     Outcome = failureClass == DevWorkflowFailureClasses.Timeout ? DevWorkflowOutcomes.Timeout : null
                                 },
                                 cancellationToken);
    }

    /// <summary>Asks a node run's commands to stop, and answers whether there was anything to ask.</summary>
    /// <remarks>
    ///     A pass ALREADY asked has nothing left to ask of it, so the answer is no — which is load-bearing, not tidy:
    ///     a yes each tick spins a cancelling drain for as long as the commands take. The row is deliberately NOT
    ///     settled here. See docs/wiki/25-dev-workflows.md ("The tool lane").
    /// </remarks>
    public async Task<bool> StopAsync(Guid nodeRunId)
    {
        // ponytail: the run waits up to one sweep (DevWorkflowOptions.SweepSeconds) to notice a stopped pass landed,
        // since signalling from its continuation needs the dispatcher, which takes THIS type. Fix: a settable signal.
        if (!_inflight.TryGetValue(nodeRunId, out var flight) || flight.Cancellation.IsCancellationRequested)
        {
            return false;
        }

        await flight.Cancellation.CancelAsync();
        return true;
    }

    /// <summary>Drops every pass whose row has moved on and is no longer this lane's to settle.</summary>
    /// <remarks>
    ///     Called once a tick before anything is polled, because a reset reaches rows this lane is driving WITHOUT
    ///     coming through it. See docs/wiki/25-dev-workflows.md ("The tool lane").
    /// </remarks>
    public async Task ForgetSupersededAsync(IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns)
    {
        ArgumentNullException.ThrowIfNull(nodeRuns);

        foreach (var nodeRun in nodeRuns)
        {
            if (_inflight.TryGetValue(nodeRun.Id, out var flight)
                && (flight.Attempt != nodeRun.Attempt || nodeRun.Status is not (DevWorkflowNodeRunStatus.Queued or DevWorkflowNodeRunStatus.Running)))
            {
                await DiscardAsync(nodeRun.Id);
            }
        }
    }

    /// <summary>Drops one pass whose row has moved on, so no attempt settles from an answer about the one before.</summary>
    /// <remarks>
    ///     Removing the entry is the load-bearing half, not the cancel. The result is thrown away deliberately and
    ///     the pass is left to unwind on its own. See docs/wiki/25-dev-workflows.md ("The tool lane").
    /// </remarks>
    public async Task DiscardAsync(Guid nodeRunId)
    {
        if (!_inflight.TryRemove(nodeRunId, out var flight))
        {
            return;
        }

        await flight.Cancellation.CancelAsync();
        _ = DisposeWhenDoneAsync(flight);
    }

    /// <summary>Disposes a discarded pass's cancellation once the pass has actually stopped using its token.</summary>
    private static async Task DisposeWhenDoneAsync(InFlight flight)
    {
        await SwallowAsync(flight.Work);
        flight.Cancellation.Dispose();
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

        await _shutdown.CancelAsync();
        foreach (var flight in _inflight.Values)
        {
            await SwallowAsync(flight.Work);
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
        return new InFlight { Cancellation = cancellation, Work = RunAsync(run, node, nodeRun, cancellation.Token), Attempt = nodeRun.Attempt };
    }

    private async Task<DevWorkflowToolRun> RunAsync(DevWorkflowRunSnapshot run,
        DevWorkflowGraphNode node,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            if (node.ToolMode == DevWorkflowToolMode.Apply)
            {
                // The integration variant takes the same lane slot as a validation pass, deliberately: same workspace
                // machinery, same repository, which is the resource the slot count bounds.
                return scope.ServiceProvider.GetService<DevWorkflowApplyCommands>() is { } apply
                    ? await apply.RunAsync(run, nodeRun, cancellationToken)
                    : Refused(DevWorkflowFailureClasses.Configuration,
                        "This node applies approved patches, and Development Mode is switched off on this node.");
            }

            if (scope.ServiceProvider.GetService<IDevWorkflowToolCommands>() is not { } commands)
            {
                // Development Mode is switched off on this node, so there is no workspace provider, no repository
                // binding and no sandbox. Nothing here can run, and no retry changes that.
                return Refused(DevWorkflowFailureClasses.Configuration,
                    "This node runs repository commands, and Development Mode is switched off on this node.");
            }

            return await commands.RunAsync(run, node, nodeRun, cancellationToken);
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

            // Said in the node's own terms: this lane runs both kinds, and "validation commands stopped" is the wrong
            // account of the node that puts approved patches into the operator's repository.
            return Refused(DevWorkflowFailureClasses.Internal,
                node.ToolMode == DevWorkflowToolMode.Apply
                    ? "This node run stopped on an unexpected error while applying approved patches. The engine log has the detail."
                    : "This node run's validation commands stopped on an unexpected error. The engine log has the detail.");
        }
        finally
        {
            _ = _lane.Release();
        }
    }

    /// <summary>
    ///     Whether the row belongs to the integration variant of this lane. A row whose node the run's graph no longer
    ///     declares reads as the ordinary kind.
    /// </summary>
    private static bool IsApplying(DevWorkflowGraph graph, DevWorkflowNodeRunSnapshot nodeRun) =>
        graph.Nodes.TryGetValue(nodeRun.NodeKey, out var node) && node.ToolMode == DevWorkflowToolMode.Apply;

    /// <summary>What a pass stopped from outside is told to have been doing, in the node's OWN terms.</summary>
    /// <remarks>
    ///     This lane runs two kinds of work, and "validation commands" is the wrong account of the node that puts
    ///     approved patches into a repository — the node where what was and was not done matters most.
    /// </remarks>
    private static string StoppedReason(DevWorkflowGraph graph, DevWorkflowNodeRunSnapshot nodeRun, string what) =>
        IsApplying(graph, nodeRun)
            ? $"{what} while this node run was applying approved patches."
            : $"{what} while this node run was running its validation commands.";

    private static DevWorkflowToolRun Refused(string failureClass, string sanitizedReason) =>
        new()
        {
            Passed = false,
            FailureClass = failureClass,
            FailureCode = null,
            SanitizedReason = sanitizedReason,
            CommandsRun = 0,
            CommandsFailed = 0,
            TestsPassed = null,
            TestsFailed = null,
            Report = ReadOnlyMemory<byte>.Empty,
            SecretPaths = []
        };

    private static async Task<int> RunningAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Running, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
        {
            RunId = run.Id,
            NodeRunId = nodeRun.Id,
            ExpectedVersion = DevWorkflowVersions.Any,
            TargetStatus = DevWorkflowNodeRunStatus.Running
        },
                           cancellationToken);
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

        _ = await store.AppendEventAsync(new AppendDevWorkflowEventCommand
        {
            RunId = run.Id,
            ExpectedVersion = DevWorkflowVersions.Any,
            EventType = DevWorkflowEventTypes.WorkspaceSecretsDetected,
            NodeRunId = nodeRun.Id,
            OperationId = DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "workspace-secrets"),
            DetailJson = JsonSerializer.Serialize(new SecretsDetail { Paths = result.SecretPaths }, JsonOptions)
        },
                           cancellationToken);
    }

    /// <summary>Writes the node run's report into the run's artifacts, so the evidence outlives its workspace.</summary>
    /// <remarks>
    ///     Keyed on <c>(run, node key, attempt)</c>, so a replayed poll rewrites the same blob and the store's
    ///     query-first check returns the recorded result instead of appending a second version. An apply node's
    ///     report is a different document about a different act, written under the ordinary <c>Report</c> kind and
    ///     its own name, so a reader decoding a validation report is never handed one that is not.
    /// </remarks>
    private async Task PromoteReportAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
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

        var apply = IsApplying(graph, nodeRun);
        var artifactId = DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "validation-report");
        var write = await _blobs.WriteAsync(run.Id, artifactId, result.Report, cancellationToken);
        var appended = await store.AppendArtifactAsync(new AppendDevWorkflowArtifactCommand
        {
            RunId = run.Id,
            ArtifactId = artifactId,
            NodeRunId = nodeRun.Id,
            ExpectedVersion = DevWorkflowVersions.Any,
            OperationId = DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "report"),
            Kind = apply ? DevWorkflowArtifactKind.Report : DevWorkflowArtifactKind.ValidationReport,
            Name = apply ? $"{nodeRun.NodeKey}-apply.json" : $"{nodeRun.NodeKey}-validation.json",
            MediaType = "application/json",
            ContentSha256 = write.ContentHash,
            SizeBytes = write.ByteCount,
            ManagedReference = write.OpaqueReference
        },
                                      cancellationToken);

        if (appended.SupersededArtifactId is not { } superseded)
        {
            return;
        }

        // A re-attempt's report replaces the one a downstream node may already have read. Mark-only: nothing is
        // regenerated, and a human decides what a stale consumer is worth.
        _ = await store.MarkDependentsStaleAsync(new MarkDevWorkflowStaleCommand
        {
            RunId = run.Id,
            SupersededArtifactId = superseded,
            SupersedingArtifactId = artifactId,
            ExpectedVersion = DevWorkflowVersions.Any,
            OperationId = DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "report-stale")
        },
                           cancellationToken);
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
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
        {
            RunId = run.Id,
            NodeRunId = nodeRun.Id,
            ExpectedVersion = DevWorkflowVersions.Any,
            // A node run standing down for a human names the answer it is waiting for, the same way
            // every other blocked row does.
            TargetStatus = target,
            PendingDecisionKind = target == DevWorkflowNodeRunStatus.Blocked ? DevWorkflowDecisionKind.Abandon : null,
            OutputJson = outputJson,
            FailureClass = failureClass,
            TerminalReason = terminalReason,
            Outcome = outcome,
            WorkItemStatus = target == DevWorkflowNodeRunStatus.Blocked
                                   ? DevWorkflowWorkItemStatus.Blocked
                                   : DevWorkflowStateMachine.WorkItemStatusAfter(run.Status, nodeRuns, nodeRun.Id, target)
        },
                           cancellationToken);
        return 1;
    }

    /// <summary>The tool node's slice of the output document every executor writes.</summary>
    /// <remarks>
    ///     The verdict a conditional edge routes on, and the counts a fix-loop objective quotes. No command text: an
    ///     output document is routing data, and the evidence lives in the report artifact.
    /// </remarks>
    private static string Output(DevWorkflowNodeRunSnapshot nodeRun, string? failureClass, DevWorkflowToolRun? run) =>
        JsonSerializer.Serialize(new ToolOutput
        {
            Status = run is { Passed: true } ? DevWorkflowNodeOutputStatuses.Succeeded : DevWorkflowNodeOutputStatuses.Failed,
            Attempt = nodeRun.Attempt,
            FailureClass = failureClass,
            Passed = run?.Passed ?? false,
            FailureCode = run?.FailureCode,
            CommandsRun = run?.CommandsRun ?? 0,
            CommandsFailed = run?.CommandsFailed ?? 0,
            TestsPassed = run?.TestsPassed,
            TestsFailed = run?.TestsFailed
        },
            JsonOptions);

    /// <summary>Awaits a detached pass without letting its outcome escape; the poll is what reads that.</summary>
    private static async Task SwallowAsync(Task work)
    {
        try
        {
            await work;
        }
        catch (Exception)
        {
            // Every outcome is the poll's to read off the task, including this one.
        }
    }

    /// <summary>
    ///     One detached pass. <see cref="Attempt" /> is what makes the entry belong to an ATTEMPT rather than to a row:
    ///     the fix loop can re-attempt a node run this lane is driving, and an answer about the attempt before is not an
    ///     answer about this one.
    /// </summary>
    private sealed record InFlight
    {
        public required CancellationTokenSource Cancellation { get; init; }

        public required Task<DevWorkflowToolRun> Work { get; init; }

        public required int Attempt { get; init; }
    }

    private sealed record SecretsDetail
    {
        public required IReadOnlyList<string> Paths { get; init; }
    }

    private sealed record ToolOutput
    {
        public required string Status { get; init; }

        public required int Attempt { get; init; }

        public required string? FailureClass { get; init; }

        public required bool Passed { get; init; }

        public required string? FailureCode { get; init; }

        public required int CommandsRun { get; init; }

        public required int CommandsFailed { get; init; }

        public required int? TestsPassed { get; init; }

        public required int? TestsFailed { get; init; }
    }
}
