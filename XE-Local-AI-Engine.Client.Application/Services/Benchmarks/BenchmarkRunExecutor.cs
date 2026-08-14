namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Providers.LlamaServer;

public sealed class BenchmarkRunExecutor(
    IBenchmarkStore store,
    IBenchmarkRuntimeSnapshotFactory snapshots,
    IBenchmarkInstalledModelLeaseProvider installedModels,
    ICapacityService capacity,
    ILocalChatRuntimePackageBuilder packageBuilder,
    IWorkerEventDispatcher dispatcher,
    IInvocationRunner runner,
    IBenchmarkEventBuffer events,
    IBenchmarkCancellationRegistry cancellations,
    ILogger<BenchmarkRunExecutor> logger) : IBenchmarkRunExecutor
{
    private const string FingerprintChangedMessage = "The installed model changed after the benchmark was created.";
    private const string CapacityRejectedMessage = "The benchmark could not reserve enough local model capacity.";
    private const string InvocationFailedMessage = "The benchmark invocation failed. See local logs for details.";

    public async Task ExecuteAsync(BenchmarkClaimedWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (work.Kind != BenchmarkWorkKind.Primary)
        {
            throw new ArgumentException("Primary executor received non-primary work.", nameof(work));
        }

        using var registration = cancellations.Register(work.RunId, BenchmarkWorkKind.Primary, cancellationToken);
        var token = registration.Token;
        try
        {
            var snapshot = snapshots.Deserialize(work.Run.RuntimeSnapshotJson.Span);
            await using var modelLease = await installedModels.AcquireAsync(snapshot.PrimaryModel.ModelName, token).ConfigureAwait(false);
            if (!BenchmarkSnapshotModelComparer.Matches(snapshot.PrimaryModel, modelLease.Snapshot))
            {
                throw new BenchmarkExecutionException(FingerprintChangedMessage);
            }

            var decision = await capacity.DecideAsync(new CapacityRequest(snapshot.PrimaryModel.ModelName,
                    ModelRole.Chat,
                    snapshot.RequestedContextTokens), token)
                .ConfigureAwait(false);
            if (decision.Verdict == CapacityVerdict.RejectInsufficient)
            {
                throw new BenchmarkExecutionException(CapacityRejectedMessage);
            }

            using var reservation = decision.Reservation;
            var package = BuildPrimaryPackage(snapshot);
            var admission = new BenchmarkContextAdmissionPolicy(snapshot.RequestedContextTokens);
            using var capture = new BenchmarkInvocationCapture(work.RunId, package.InvocationId, dispatcher, events);
            events.Append(work.RunId,
                BenchmarkRunStreamEventKind.PrimaryState,
                new BenchmarkRunStreamPayload(State: BenchmarkPrimaryStatus.Running.ToString()));

            await using var assignment = await dispatcher.ReportInvocationAssignedAsync(package, token).ConfigureAwait(false);
            using var context = InvocationExecutionContext.CreatePlain(package,
                Guid.Empty,
                generationAdmissionPolicy: admission);
            await runner.RunAsync(context, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var terminal = capture.TerminalState;
            if (terminal?.Status != InvocationStatus.Completed)
            {
                throw new BenchmarkExecutionException(InvocationFailedMessage);
            }

            var effectiveContext = admission.EffectiveContextTokens
                                   ?? throw new BenchmarkExecutionException("The effective model context was unavailable.");
            var durationMs = terminal.GenerationDurationMs ?? 0;
            double? tokensPerSecond = terminal.TotalTokens is { } total && durationMs > 0
                ? total * 1000d / durationMs
                : null;
            var metricsEvent = events.Reserve(work.RunId,
                BenchmarkRunStreamEventKind.Metrics,
                new BenchmarkRunStreamPayload(EffectiveContextTokens: effectiveContext,
                    DurationMs: durationMs,
                    TotalTokens: terminal.TotalTokens,
                    TokensPerSecond: tokensPerSecond));
            var terminalEvent = events.Reserve(work.RunId,
                BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
                new BenchmarkRunStreamPayload(State: BenchmarkPrimaryStatus.Succeeded.ToString(), RunVersion: work.Run.Version + 1));
            var persisted = await store.MarkPrimarySucceededAsync(new BenchmarkPrimarySuccessCommand(work.RunId,
                    work.Run.Version,
                    BenchmarkExecutionSerialization.SerializeParts(capture.Parts),
                    terminalEvent.Sequence,
                    effectiveContext,
                    durationMs,
                    terminal.TotalTokens,
                    tokensPerSecond), CancellationToken.None)
                .ConfigureAwait(false);
            events.PublishReserved(metricsEvent);
            events.PublishReserved(terminalEvent with { Payload = terminalEvent.Payload with { RunVersion = persisted.Version } });
            events.EvictPlaintext(work.RunId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            events.EvictPlaintext(work.RunId);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TerminalizeCancelledAsync(work.RunId).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Benchmark primary work {RunId} failed.", work.RunId);
            await TerminalizeFailedAsync(work.RunId, exception is BenchmarkExecutionException safe ? safe.Message : InvocationFailedMessage).ConfigureAwait(false);
        }
    }

    private RuntimePackage BuildPrimaryPackage(BenchmarkRuntimeSnapshotV1 snapshot)
    {
        var runtime = snapshot.ResolvedRuntime;
        return packageBuilder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            Guid.NewGuid(),
            runtime.ResolvedSystemPrompt,
            [new ConversationMessageDto
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.User,
                Content = snapshot.CoreTask,
                SortOrder = 0
            }],
            snapshot.PrimaryModel.ModelName,
            runtime.AgentDefinitionVersion,
            LocalChatLoopbackDefaults.ClientNodeId,
            runtime.AllowedTools,
            RequestedCapabilities: [LocalChatLoopbackDefaults.RequestedCapability],
            ReasoningEffort: runtime.ReasoningEffort,
            SamplingOptions: new SamplingOptions { NumCtx = snapshot.RequestedContextTokens },
            Skills: runtime.Skills,
            IsUnattended: true,
            CustomTools: runtime.CustomTools));
    }

    private async Task TerminalizeCancelledAsync(Guid runId)
    {
        var run = await store.GetRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
        if (run is null || run.PrimaryStatus == BenchmarkPrimaryStatus.Cancelled)
        {
            events.EvictPlaintext(runId);
            return;
        }

        if (run.PrimaryStatus is not (BenchmarkPrimaryStatus.Running or BenchmarkPrimaryStatus.CancelRequested))
        {
            events.EvictPlaintext(runId);
            return;
        }

        var terminal = events.Reserve(runId,
            BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
            new BenchmarkRunStreamPayload(State: BenchmarkPrimaryStatus.Cancelled.ToString(), RunVersion: run.Version + 1));
        var persisted = await store.MarkPrimaryCancelledAsync(runId, run.Version, terminal.Sequence, CancellationToken.None).ConfigureAwait(false);
        events.PublishReserved(terminal with { Payload = terminal.Payload with { RunVersion = persisted.Version } });
        events.EvictPlaintext(runId);
    }

    private async Task TerminalizeFailedAsync(Guid runId, string message)
    {
        var run = await store.GetRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
        if (run is null || run.PrimaryStatus is BenchmarkPrimaryStatus.Succeeded or BenchmarkPrimaryStatus.Failed or BenchmarkPrimaryStatus.Cancelled)
        {
            events.EvictPlaintext(runId);
            return;
        }

        var terminal = events.Reserve(runId,
            BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
            new BenchmarkRunStreamPayload(State: BenchmarkPrimaryStatus.Failed.ToString(), RunVersion: run.Version + 1));
        var persisted = await store.MarkPrimaryFailedAsync(runId, run.Version, message, terminal.Sequence, CancellationToken.None).ConfigureAwait(false);
        events.PublishReserved(terminal with { Payload = terminal.Payload with { RunVersion = persisted.Version } });
        events.EvictPlaintext(runId);
    }
}

internal sealed class BenchmarkInvocationCapture : IDisposable
{
    private readonly Guid _runId;
    private readonly Guid _invocationId;
    private readonly IWorkerEventDispatcher _dispatcher;
    private readonly IBenchmarkEventBuffer _events;
    private readonly Lock _gate = new();
    private readonly List<BenchmarkOutputPart> _parts = [];
    private int _contentLength;
    private int _reasoningLength;

    public BenchmarkInvocationCapture(Guid runId,
        Guid invocationId,
        IWorkerEventDispatcher dispatcher,
        IBenchmarkEventBuffer events)
    {
        _runId = runId;
        _invocationId = invocationId;
        _dispatcher = dispatcher;
        _events = events;
        dispatcher.InvocationStateChanged += OnInvocationStateChanged;
        dispatcher.ToolCallLifecycleChanged += OnToolCallLifecycleChanged;
    }

    public InvocationState? TerminalState { get; private set; }

    public IReadOnlyList<BenchmarkOutputPart> Parts
    {
        get
        {
            lock (_gate)
            {
                return _parts.ToArray();
            }
        }
    }

    public void Dispose()
    {
        _dispatcher.InvocationStateChanged -= OnInvocationStateChanged;
        _dispatcher.ToolCallLifecycleChanged -= OnToolCallLifecycleChanged;
    }

    private void OnInvocationStateChanged(object? sender, InvocationStateChangedEventArgs args)
    {
        var state = args.State;
        if (state.InvocationId != _invocationId)
        {
            return;
        }

        lock (_gate)
        {
            AppendTextDelta(state.StreamedContent, ref _contentLength, "output", BenchmarkRunStreamEventKind.OutputDelta);
            AppendTextDelta(state.StreamedThinkingContent, ref _reasoningLength, "reasoning", BenchmarkRunStreamEventKind.ReasoningDelta);
            if (state.Status is InvocationStatus.Completed or InvocationStatus.Failed or InvocationStatus.Cancelled)
            {
                TerminalState = state;
            }
        }
    }

    private void AppendTextDelta(string current,
        ref int priorLength,
        string partKind,
        BenchmarkRunStreamEventKind eventKind)
    {
        if (current.Length <= priorLength)
        {
            priorLength = current.Length;
            return;
        }

        var delta = current[priorLength..];
        priorLength = current.Length;
        _parts.Add(new BenchmarkOutputPart(partKind, Content: delta));
        _events.Append(_runId, eventKind, new BenchmarkRunStreamPayload(Content: delta));
    }

    private void OnToolCallLifecycleChanged(object? sender, ToolCallLifecycleChangedEventArgs args)
    {
        var payload = args.Payload;
        if (payload.InvocationId != _invocationId)
        {
            return;
        }

        lock (_gate)
        {
            var requested = payload.Phase == ToolCallLifecyclePhase.Requested;
            _parts.Add(new BenchmarkOutputPart(requested ? "tool_call" : "tool_result",
                ToolCallId: payload.ToolCallId,
                ToolName: payload.ToolName,
                Arguments: requested ? payload.Arguments : null,
                Result: requested ? null : payload.Result,
                IsError: requested ? null : payload.IsError));
            _events.Append(_runId,
                requested ? BenchmarkRunStreamEventKind.ToolCall : BenchmarkRunStreamEventKind.ToolResult,
                new BenchmarkRunStreamPayload(ToolCallId: payload.ToolCallId,
                    ToolName: payload.ToolName,
                    Arguments: requested ? payload.Arguments : null,
                    Result: requested ? null : payload.Result,
                    IsError: requested ? null : payload.IsError));
        }
    }
}

internal sealed class BenchmarkExecutionException(string message) : InvalidOperationException(message);
