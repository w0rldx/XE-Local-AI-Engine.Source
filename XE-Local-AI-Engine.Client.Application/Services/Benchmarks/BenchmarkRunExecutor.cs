namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public sealed class BenchmarkRunExecutor(
    IBenchmarkStore store,
    IBenchmarkRuntimeSnapshotFactory snapshots,
    IBenchmarkInstalledModelLeaseProvider installedModels,
    ICapacityService capacity,
    ILocalChatRuntimePackageBuilder packageBuilder,
    IWorkerEventDispatcher dispatcher,
    IInvocationRunner runner,
    ILlamaServerProcessSupervisor supervisor,
    IGpuVariantSelector variantSelector,
    ILlamaServerEndpointBinding endpointBinding,
    IBenchmarkEventBuffer events,
    IBenchmarkCancellationRegistry cancellations,
    IRuntimeEnvironmentFactsProvider environmentFacts,
    IBenchmarkJudgeRuntimeResolver judgeRuntimeResolver,
    IBenchmarkPairwisePlanner pairwisePlanner,
    BenchmarkAdmissionRetry admissionRetry,
    ILogger<BenchmarkRunExecutor> logger) : IBenchmarkRunExecutor
{
    private const string FingerprintChangedMessage = "The installed model changed after the benchmark was created.";
    private const string CapacityRejectedMessage = "The benchmark could not reserve enough local model capacity.";
    private const string InvocationFailedMessage = "The benchmark invocation failed. See local logs for details.";
    private const string JudgePolicyChangedMessage = "judge policy changed during run";

    /// <summary>How many times a judge-policy change is re-resolved before the first attempt is committed as failed.</summary>
    private const int JudgePolicyResolutionAttempts = 3;

    public async Task ExecuteAsync(BenchmarkClaimedWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (work.Kind != BenchmarkWorkKind.Primary)
        {
            throw new ArgumentException("Primary executor received non-primary work.", nameof(work));
        }

        using var registration = cancellations.Register(work.RunId, BenchmarkWorkKind.Primary, cancellationToken);
        var token = registration.Token;
        RuntimeEnvironmentFactsV1? environment = null;
        try
        {
            var snapshot = snapshots.Deserialize(work.Run.RuntimeSnapshotJson.Span);

            // D14: a row frozen under a different launch-identity scheme is failed BEFORE anything is leased or
            // spawned, so it never writes an effective identity that could not be compared to its intended one.
            BenchmarkLaunchIdentityScheme.RequireCurrent(work.Run.PrimaryLaunchIntent);
            await using var modelLease = await installedModels.AcquireAsync(snapshot.PrimaryModel.ModelName, token).ConfigureAwait(false);
            if (!BenchmarkSnapshotModelComparer.Matches(snapshot.PrimaryModel, modelLease.Snapshot))
            {
                throw new BenchmarkExecutionException(FingerprintChangedMessage);
            }

            // The host facts this measurement was taken on, captured before anything is reserved or spawned so a
            // failed launch still carries them. Non-throwing by contract.
            environment = await environmentFacts.CaptureAsync(snapshot.PrimaryRuntime.Variant, token).ConfigureAwait(false);

            // Admission sizes against the context the FROZEN runtime will actually launch with, not the context the
            // project requested: a profile replay can pin a larger window, and reserving the smaller one under-books.
            // It also sizes the KV term against the FROZEN KV-cache type (null ⇒ f16), so a q8_0/q4_0 run books the
            // bytes it will really hold rather than the f16 figure it will never reach.
            // No launch admission: this run spawns its own exclusive process from the FROZEN replay arguments, so an
            // admission published here is one nothing ever consumes — and the supervisor refuses to launch against it.
            // A rejection is transient by nature — it means something holds the bytes RIGHT NOW — so the phase waits
            // and re-decides on a cadence instead of terminalizing the run on the first no.
            // ONE budget for the whole phase: the capacity wait below and the exclusive-spawn wait after it both draw
            // from it, so a phase cannot hold the queue's shared GPU-work admission for two full budgets.
            var waitBudget = new BenchmarkWaitBudget(admissionRetry);
            var decision = await BenchmarkCapacityAdmission.AdmitAsync(capacity,
                                                               new CapacityRequest(snapshot.PrimaryModel.ModelName,
                                                                   ModelRole.Chat,
                                                                   snapshot.PrimaryRuntime.ContextTokens,
                                                                   PublishLaunchAdmission: false,
                                                                   snapshot.PrimaryRuntime.KvTypeK),
                                                               new BenchmarkAdmissionContext(work.RunId,
                                                                   "primary",
                                                                   snapshot.RequestedContextTokens,
                                                                   snapshot.PrimaryRuntime.KvTypeK ?? BenchmarkKvCacheType.F16,
                                                                   CapacityRejectedMessage),
                                                               waitBudget,
                                                               logger,
                                                               token)
                                                           .ConfigureAwait(false);

            using var reservation = decision.Reservation;
            var package = BuildPrimaryPackage(snapshot, work.Run.InvocationTimeoutSeconds);
            var admission = new BenchmarkContextAdmissionPolicy(snapshot.RequestedContextTokens);
            using var capture = new BenchmarkInvocationCapture(work.RunId, package.InvocationId, dispatcher, events);
            events.Append(work.RunId,
                BenchmarkRunStreamEventKind.PrimaryState,
                new BenchmarkRunStreamPayload(State: BenchmarkPrimaryStatus.Running.ToString()));

            var currentVariant = await variantSelector.SelectVariantAsync(token).ConfigureAwait(false);
            if (currentVariant != snapshot.PrimaryRuntime.Variant)
            {
                throw new BenchmarkExecutionException("The selected llama.cpp runtime changed after the benchmark was created.");
            }

            // A model still serving a request refuses the pre-spawn eviction. That is a transient the request clears
            // itself, so the spawn waits and retries rather than terminalizing this run — see BenchmarkExclusiveSpawn.
            _ = await BenchmarkExclusiveSpawn.RunAsync(spawnToken =>
                                    supervisor.RunExclusiveBenchmarkAsync(snapshot.PrimaryModel.ModelName,
                                        ModelRole.Chat,
                                        snapshot.PrimaryRuntime.ToResolvedLaunchArguments(),
                                        snapshot.PrimaryRuntime.LaunchPolicy,
                                        async (profiling, profilingToken) =>
                                        {
                                            // Durable BEFORE any token is generated: a run that reached readiness keeps
                                            // its evidence no matter how the invocation ends.
                                            await CheckpointAsync(work, profiling.LaunchReceipt, environment).ConfigureAwait(false);
                                            using var endpointScope = endpointBinding.Bind(profiling.Endpoint);
                                            await using var assignment = await dispatcher.ReportInvocationAssignedAsync(package, profilingToken).ConfigureAwait(false);
                                            using var context = InvocationExecutionContext.CreatePlain(package,
                                                Guid.Empty,
                                                generationAdmissionPolicy: admission);
                                            await runner.RunAsync(context, profilingToken).ConfigureAwait(false);
                                            return true;
                                        },
                                        spawnToken),
                                    waitBudget,
                                    work.RunId,
                                    "primary",
                                    logger,
                                    token)
                                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var terminal = capture.TerminalState;
            if (terminal?.Status != InvocationStatus.Completed)
            {
                // A run the node cancelled at its own invocation budget is the one failure that can explain itself, and
                // "the invocation failed" is exactly the message that made the live 307 s cancellation unattributable.
                throw new BenchmarkExecutionException(InvocationFailedMessage)
                {
                    StopReason = terminal?.FailureCategory == FailureCategory.Timeout ? BenchmarkPrimaryStopReasons.Timeout : null
                };
            }

            var effectiveContext = admission.EffectiveContextTokens
                                   ?? throw new BenchmarkExecutionException("The effective model context was unavailable.");

            // Coalesced HERE, once: the live stream needs one event per delta, storage needs one part per contiguous
            // run (see BenchmarkOutputParts), and the stop-reason verdict below has to read the SHAPE of the turn,
            // which a per-delta capture does not show.
            var parts = BenchmarkOutputParts.Coalesce(capture.Parts);
            var stopReason = ResolveStopReason(terminal.FinishReason, parts);
            var durationMs = terminal.GenerationDurationMs ?? 0;
            var throughput = ToThroughput(terminal.Throughput);

            // tokens/s now MEANS decode throughput (tg) whenever the runtime timed the prompt and the decode
            // separately: dividing the turn's total tokens by its wall clock blends prefill into generation, so the same
            // model measured on a long prompt and a short one produced two incomparable numbers. The blended figure
            // remains the fallback for a runtime that reports no timings, so the column never goes empty.
            var tokensPerSecond = throughput?.GenerationTokensPerSecond ?? TokenThroughput.FromMilliseconds(terminal.TotalTokens, durationMs);
            var metricsEvent = events.Reserve(work.RunId,
                BenchmarkRunStreamEventKind.Metrics,
                new BenchmarkRunStreamPayload(EffectiveContextTokens: effectiveContext,
                    DurationMs: durationMs,
                    TotalTokens: terminal.TotalTokens,
                    TokensPerSecond: tokensPerSecond,
                    TtftMs: throughput?.TtftMs,
                    PromptTokens: throughput?.PromptTokens,
                    PromptTokensPerSecond: throughput?.PromptTokensPerSecond,
                    GenerationTokens: throughput?.GenerationTokens,
                    GenerationTokensPerSecond: throughput?.GenerationTokensPerSecond,
                    CachedPromptTokens: throughput?.CachedPromptTokens,
                    SegmentCount: throughput?.SegmentCount));
            var terminalEvent = events.Reserve(work.RunId,
                BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
                new BenchmarkRunStreamPayload(State: BenchmarkPrimaryStatus.Succeeded.ToString(), RunVersion: work.Run.Version + 1));
            var persisted = await MarkPrimarySucceededAsync(work,
                    new BenchmarkPrimarySuccessCommand(work.RunId,
                        work.Version,
                        BenchmarkExecutionSerialization.SerializeParts(parts),
                        terminalEvent.Sequence,
                        effectiveContext,
                        durationMs,
                        terminal.TotalTokens,
                        tokensPerSecond,
                        // A generation cut off at the token budget still SUCCEEDS — the measurement is real — but the
                        // run has to carry why it stopped, or the ranking and the judge grade an incomplete answer as
                        // if it were a finished one.
                        stopReason,
                        Throughput: throughput))
                .ConfigureAwait(false);
            events.PublishReserved(metricsEvent);
            events.PublishReserved(terminalEvent with
            {
                Payload = terminalEvent.Payload with
                {
                    RunVersion = persisted.Version
                }
            });
            events.EvictPlaintext(work.RunId);

            // A newly succeeded run is newly eligible to be compared against every other one. In pointwise mode this
            // is a no-op; in pairwise mode it is the second of the three places a cohort grows, and it is incremental
            // — one more run enqueues 2N comparisons, not the whole tournament again.
            _ = await pairwisePlanner.EnsurePairsAsync(work.Run.ProjectId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            events.EvictPlaintext(work.RunId);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TerminalizeCancelledAsync(work, environment).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Benchmark primary work {RunId} failed.", work.RunId);
            await TerminalizeFailedAsync(work,
                exception is BenchmarkExecutionException or LlamaRuntimeException ? exception.Message : InvocationFailedMessage,
                environment,
                (exception as BenchmarkExecutionException)?.StopReason).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     The stop reason to persist: the provider's own token, unless the turn finished cleanly having produced no
    ///     answer, which is recorded as <see cref="BenchmarkPrimaryStopReasons.Incomplete" />.
    ///     <para>
    ///         The provider cannot report this. A turn that stopped on an unanswered tool call reports
    ///         <c>tool_calls</c>, and a thinking model that spent the whole turn reasoning reports <c>stop</c> — both
    ///         read downstream as a finished answer, so the judge graded an empty transcript and the ranking seated its
    ///         score beside runs that actually answered. A <c>length</c> stop keeps its own token: that run IS cut off,
    ///         and <see cref="BenchmarkPrimaryStopReasons.IsTruncated" /> already excludes and annotates it — except
    ///         when it never emitted an answer either, which is recorded as
    ///         <see cref="BenchmarkPrimaryStopReasons.ReasoningLength" />: still truncated for every consumer, but it
    ///         names the reasoning budget as the thing to raise rather than the output budget.
    ///     </para>
    /// </summary>
    internal static string? ResolveStopReason(string? finishReason, IReadOnlyList<BenchmarkOutputPart> parts)
    {
        if (BenchmarkPrimaryStopReasons.IsTruncated(finishReason))
        {
            // Out of budget having never left the scratchpad: the same cut-off run, but the operator's fix is a
            // reasoning budget rather than a bigger output budget, and only this distinction says which.
            return BenchmarkOutputParts.HasAnswerText(parts) ? finishReason : BenchmarkPrimaryStopReasons.ReasoningLength;
        }

        return BenchmarkOutputParts.IsUnanswered(parts) ? BenchmarkPrimaryStopReasons.Incomplete : finishReason;
    }

    // Crosses the layer boundary by hand rather than by a shared type: the throughput measurement is produced in the
    // invocation layer and persisted by the store, and neither may reference the other's contract.
    private static BenchmarkRunThroughput? ToThroughput(InvocationThroughput? throughput) =>
        throughput is null
            ? null
            : new BenchmarkRunThroughput(throughput.TimeToFirstTokenMs,
                throughput.PromptTokens,
                throughput.PromptMs,
                throughput.GenerationTokens,
                throughput.GenerationMs,
                throughput.CachedPromptTokens,
                throughput.SegmentCount);

    /// <summary>
    ///     Commits primary success together with the run's first judging, in the store's single transaction. The judge
    ///     runtime is resolved for one specific policy revision BEFORE the call; if the project moved to another
    ///     revision in the meantime the store rolls the whole thing back, and this re-resolves against the new one. A
    ///     bounded number of rounds, then the attempt is committed as failed — the measurement is never lost to a
    ///     judge-configuration race, and the operator re-judges.
    /// </summary>
    private async Task<BenchmarkRunRecord> MarkPrimarySucceededAsync(BenchmarkClaimedWork work, BenchmarkPrimarySuccessCommand command)
    {
        for (var round = 1; round <= JudgePolicyResolutionAttempts; round++)
        {
            var revision = await store.GetCurrentJudgePolicyRevisionAsync(work.Run.ProjectId, CancellationToken.None).ConfigureAwait(false);
            if (revision is null)
            {
                return await store.MarkPrimarySucceededAsync(command, CancellationToken.None).ConfigureAwait(false);
            }

            try
            {
                return await store.MarkPrimarySucceededAsync(command with
                {
                    JudgeAttempt = await ResolveJudgeSeedAsync(revision).ConfigureAwait(false)
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (BenchmarkJudgePolicyChangedException) when (round < JudgePolicyResolutionAttempts)
            {
                logger.LogInformation("Benchmark run {RunId}: the judge policy changed while the run was executing; re-resolving.", work.RunId);
            }
        }

        // The bounded retry is exhausted. Still atomic: the run keeps its measurement and carries a failed first
        // attempt the operator can re-judge, rather than losing the measurement to a judge-configuration race.
        return await store.MarkPrimarySucceededAsync(command with
        {
            JudgeAttempt = new BenchmarkJudgeAttemptSeed(ExpectedJudgePolicyRevisionId: null, RuntimeJson: null, JudgePolicyChangedMessage)
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    ///     The judge runtime for <paramref name="revision" />, or a seed that records why it could not be resolved.
    ///     Never throws toward the primary result: an unusable judge runtime is a failed judging, not a failed run.
    /// </summary>
    private async Task<BenchmarkJudgeAttemptSeed> ResolveJudgeSeedAsync(BenchmarkJudgePolicyRevisionRecord revision)
    {
        try
        {
            var policy = BenchmarkJudgeSerialization.DeserializePolicy(revision.PolicyJson!.Value.Span);
            var resolution = await judgeRuntimeResolver.ResolveAsync(policy, CancellationToken.None).ConfigureAwait(false);
            return new BenchmarkJudgeAttemptSeed(revision.Id,
                new ReadOnlyMemory<byte>(BenchmarkJudgeSerialization.SerializeRuntime(resolution.Runtime)),
                RuntimeUnresolvedReason: null,
                resolution.Intent);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Benchmark judge runtime could not be resolved for policy revision {RevisionId}.", revision.Id);
            return new BenchmarkJudgeAttemptSeed(revision.Id, RuntimeJson: null, exception.Message);
        }
    }

    private RuntimePackage BuildPrimaryPackage(BenchmarkRuntimeSnapshotV1 snapshot, int? invocationTimeoutSeconds)
    {
        var runtime = snapshot.ResolvedRuntime;
        return packageBuilder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            Guid.NewGuid(),
            runtime.ResolvedSystemPrompt,
            [
                new ConversationMessageDto
                {
                    Id = Guid.NewGuid(),
                    Role = MessageRole.User,
                    Content = snapshot.CoreTask,
                    SortOrder = 0
                }
            ],
            snapshot.PrimaryModel.ModelName,
            runtime.AgentDefinitionVersion,
            LocalChatLoopbackDefaults.ClientNodeId,
            runtime.AllowedTools,
            RequestedCapabilities: [LocalChatLoopbackDefaults.RequestedCapability],
            Timeouts: BenchmarkFrozenPolicies.FrozenTimeouts(invocationTimeoutSeconds),
            ReasoningEffort: runtime.ReasoningEffort,
            SamplingOptions: ToSamplingOptions(snapshot.PrimarySampling, snapshot.RequestedContextTokens),
            Skills: runtime.Skills,
            IsUnattended: true,
            CustomTools: runtime.CustomTools,
            // Passed explicitly off the FROZEN model capability rather than defaulted: the default true is the safe
            // answer for a caller that does not know, and freeze does know. A model whose chat template renders no
            // reasoning end marker takes the budget and ignores it, so sending one would advertise a cap that never
            // held. Null is a run frozen before the member existed, which keeps the old default.
            ReasoningBudgetEnforceable: snapshot.PrimarySampling.ReasoningBudgetEnforceable ?? true));
    }

    internal static SamplingOptions ToSamplingOptions(BenchmarkSamplingSnapshotV1 sampling, int contextTokens) =>
        new()
        {
            // The sampler is float all the way to the wire; the snapshot keeps the double so the run's own column
            // does not record a widening artefact.
            Temperature = (float?)sampling.Temperature,
            TopP = sampling.TopP,
            TopK = sampling.TopK,
            MinP = sampling.MinP,
            MaxOutputTokens = sampling.MaxOutputTokens,
            ReasoningBudgetTokens = sampling.ReasoningBudgetTokens,
            RepeatPenalty = sampling.RepeatPenalty,
            RepeatLastN = sampling.RepeatLastN,
            PresencePenalty = sampling.PresencePenalty,
            FrequencyPenalty = sampling.FrequencyPenalty,
            Stop = sampling.Stop,
            Seed = sampling.SeedValue,
            NumCtx = contextTokens
        };

    /// <summary>
    ///     Writes the phase's launch evidence. Insert-if-null and keyed by the work item, so calling it at readiness
    ///     and again while terminalizing records the first observation and ignores the second.
    /// </summary>
    private async Task CheckpointAsync(BenchmarkClaimedWork work,
        LlamaServerLaunchReceipt? receipt,
        RuntimeEnvironmentFactsV1? environment)
    {
        var command = BenchmarkLaunchEvidence.TryBuild(receipt,
            environment,
            work.Run.PrimaryLaunchIntent?.KvCacheTypeSource ?? BenchmarkKvCacheType.SourceAuto);
        if (command is null)
        {
            return;
        }

        try
        {
            _ = await store.MarkPrimaryLaunchReadyAsync(work.RunId, work.QueueSequence, work.Version, command, CancellationToken.None)
                           .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Evidence is not worth a measurement. A version race with an operator cancel, or a busy database, must
            // not turn a healthy run into a failed one — and the token here is None, so nothing caught is a shutdown.
            logger.LogWarning(exception, "Benchmark run {RunId}: the launch-evidence checkpoint could not be recorded.", work.RunId);
        }
    }

    private async Task TerminalizeCancelledAsync(BenchmarkClaimedWork work, RuntimeEnvironmentFactsV1? environment)
    {
        await CheckpointAsync(work, receipt: null, environment).ConfigureAwait(false);
        var runId = work.RunId;
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
        var persisted = await store.MarkPrimaryCancelledAsync(runId, work.Version, terminal.Sequence, CancellationToken.None).ConfigureAwait(false);
        events.PublishReserved(terminal with
        {
            Payload = terminal.Payload with
            {
                RunVersion = persisted.Version
            }
        });
        events.EvictPlaintext(runId);
    }

    private async Task TerminalizeFailedAsync(BenchmarkClaimedWork work,
        string message,
        RuntimeEnvironmentFactsV1? environment,
        string? primaryStopReason = null)
    {
        await CheckpointAsync(work, receipt: null, environment).ConfigureAwait(false);
        var runId = work.RunId;
        var run = await store.GetRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
        if (run is null || run.PrimaryStatus is BenchmarkPrimaryStatus.Succeeded or BenchmarkPrimaryStatus.Failed or BenchmarkPrimaryStatus.Cancelled)
        {
            events.EvictPlaintext(runId);
            return;
        }

        var terminal = events.Reserve(runId,
            BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
            new BenchmarkRunStreamPayload(State: BenchmarkPrimaryStatus.Failed.ToString(), RunVersion: run.Version + 1));
        var persisted = await store.MarkPrimaryFailedAsync(runId, work.Version, message, terminal.Sequence, primaryStopReason, CancellationToken.None)
                                   .ConfigureAwait(false);
        events.PublishReserved(terminal with
        {
            Payload = terminal.Payload with
            {
                RunVersion = persisted.Version
            }
        });
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
            AppendTextDelta(state.StreamedContent, ref _contentLength, BenchmarkOutputParts.OutputKind, BenchmarkRunStreamEventKind.OutputDelta);
            AppendTextDelta(state.StreamedThinkingContent,
                ref _reasoningLength,
                BenchmarkOutputParts.ReasoningKind,
                BenchmarkRunStreamEventKind.ReasoningDelta);
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
            _parts.Add(new BenchmarkOutputPart(requested ? BenchmarkOutputParts.ToolCallKind : BenchmarkOutputParts.ToolResultKind,
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

internal sealed class BenchmarkExecutionException(string message) : InvalidOperationException(message)
{
    /// <summary>
    ///     Why generation stopped, when this failure knows — <c>timeout</c> for a run the node cancelled at its
    ///     invocation budget. Null for every failure that cannot explain itself, which then records nothing.
    /// </summary>
    public string? StopReason { get; init; }
}
