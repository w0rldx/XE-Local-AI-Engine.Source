namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Runs one judge attempt: the rubric policy the attempt was enqueued under, the judge runtime frozen onto that
///     attempt, and the durable evidence — receipt, environment facts and the rank-cohort key — recorded against the
///     attempt rather than the run, because a run is judged many times.
/// </summary>
public sealed class BenchmarkJudgeExecutor(
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
    ILogger<BenchmarkJudgeExecutor> logger) : IBenchmarkJudgeExecutor
{
    private const string FingerprintChangedMessage = "The installed judge model changed after the benchmark was created.";
    private const string CapacityRejectedMessage = "The judge could not reserve enough local model capacity.";
    private const string InvocationFailedMessage = "The benchmark judge invocation failed. See local logs for details.";

    public async Task ExecuteAsync(BenchmarkClaimedWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (work.Kind != BenchmarkWorkKind.Judge)
        {
            throw new ArgumentException("Judge executor received non-judge work.", nameof(work));
        }

        if (work.JudgeAttemptId is not { } attemptId)
        {
            throw new ArgumentException("Judge work must name the attempt it judges.", nameof(work));
        }

        using var registration = cancellations.Register(work.RunId, BenchmarkWorkKind.Judge, cancellationToken);
        var token = registration.Token;
        RuntimeEnvironmentFactsV1? environment = null;
        BenchmarkJudgeAttemptRecord? attempt = null;
        string? policyHash = null;
        try
        {
            events.BeginActivePhase(work.RunId, work.Run.LastStreamSequence);
            attempt = await store.GetJudgeAttemptAsync(attemptId, token).ConfigureAwait(false)
                      ?? throw new BenchmarkExecutionException("The judge attempt is no longer available.");
            var revision = await store.GetJudgePolicyRevisionAsync(attempt.PolicyRevisionId, token).ConfigureAwait(false)
                           ?? throw new BenchmarkExecutionException("The judge policy revision is no longer available.");
            policyHash = revision.PolicyHash;
            var policy = BenchmarkJudgeSerialization.DeserializePolicy(revision.PolicyJson!.Value.Span);
            var runtime = attempt.JudgeRuntimeJson is { } runtimeJson
                ? BenchmarkJudgeSerialization.DeserializeRuntime(runtimeJson.Span)
                : throw new BenchmarkExecutionException("The frozen judge runtime is unavailable.");

            var snapshot = snapshots.Deserialize(work.Run.RuntimeSnapshotJson.Span);
            if (work.Run.PrimaryStatus != BenchmarkPrimaryStatus.Succeeded || work.Run.OutputPartsJson is not { } output)
            {
                throw new BenchmarkExecutionException("The primary benchmark result is unavailable for judging.");
            }

            await using var modelLease = await installedModels.AcquireAsync(runtime.Model.ModelName, token).ConfigureAwait(false);
            if (!BenchmarkSnapshotModelComparer.Matches(runtime.Model, modelLease.Snapshot))
            {
                throw new BenchmarkExecutionException(FingerprintChangedMessage);
            }

            // The host facts this judging ran on, captured before anything is reserved or spawned. Non-throwing.
            environment = await environmentFacts.CaptureAsync(runtime.Runtime.Variant, token).ConfigureAwait(false);

            // Admission sizes against the frozen judge runtime's own context, not the project's request.
            // No launch admission — see BenchmarkRunExecutor: the judge spawns its own process from frozen arguments.
            var decision = await capacity.DecideAsync(new CapacityRequest(runtime.Model.ModelName,
                                             ModelRole.Chat,
                                             runtime.Runtime.ContextTokens,
                                             PublishLaunchAdmission: false), token)
                                         .ConfigureAwait(false);
            if (decision.Verdict == CapacityVerdict.RejectInsufficient)
            {
                throw new BenchmarkExecutionException(CapacityRejectedMessage);
            }

            using var reservation = decision.Reservation;
            var package = BuildJudgePackage(snapshot, policy, runtime, output.Span);
            var admission = new BenchmarkContextAdmissionPolicy(runtime.RequestedContextTokens);
            using var capture = new BenchmarkInvocationCapture(work.RunId, package.InvocationId, dispatcher, events);
            events.Append(work.RunId,
                BenchmarkRunStreamEventKind.JudgeState,
                new BenchmarkRunStreamPayload(State: BenchmarkRunJudgeStates.Running));

            var currentVariant = await variantSelector.SelectVariantAsync(token).ConfigureAwait(false);
            if (currentVariant != runtime.Runtime.Variant)
            {
                throw new BenchmarkExecutionException("The selected judge llama.cpp runtime changed after the attempt was enqueued.");
            }

            var judgingAttempt = attempt;
            var judgingPolicyHash = policyHash;
            _ = await supervisor.RunExclusiveBenchmarkAsync(runtime.Model.ModelName,
                                    ModelRole.Chat,
                                    runtime.Runtime.ToResolvedLaunchArguments(),
                                    runtime.Runtime.LaunchPolicy,
                                    async (profiling, profilingToken) =>
                                    {
                                        // Durable BEFORE any token is generated — including on an attempt an operator
                                        // cancellation has already terminalized (the successor-version clause).
                                        await CheckpointAsync(work, judgingAttempt, judgingPolicyHash, profiling.LaunchReceipt, environment).ConfigureAwait(false);
                                        using var endpointScope = endpointBinding.Bind(profiling.Endpoint);
                                        await using var assignment = await dispatcher.ReportInvocationAssignedAsync(package, profilingToken).ConfigureAwait(false);
                                        using var context = InvocationExecutionContext.CreatePlain(package,
                                            Guid.Empty,
                                            generationAdmissionPolicy: admission);
                                        await runner.RunAsync(context, profilingToken).ConfigureAwait(false);
                                        return true;
                                    },
                                    token)
                                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var terminal = capture.TerminalState;
            if (terminal?.Status != InvocationStatus.Completed)
            {
                throw new BenchmarkExecutionException(InvocationFailedMessage);
            }

            // Fail-closed parse against the attempt's own rubric, then the SERVER computes 0..100 — the judge only ever
            // scores individual criteria, so a model cannot hand itself an overall.
            var parsed = BenchmarkJudgeResultParser.Parse(terminal.StreamedContent, policy.Rubric, runtime.Model.ModelContentFingerprint);
            var terminalEvent = events.Reserve(work.RunId,
                BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
                new BenchmarkRunStreamPayload(State: BenchmarkRunJudgeStates.Succeeded, RunVersion: work.Run.Version + 1));
            var persisted = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(work.RunId,
                                           work.Version,
                                           BenchmarkJudgeSerialization.SerializeResult(parsed),
                                           terminalEvent.Sequence,
                                           parsed.Score), CancellationToken.None)
                                       .ConfigureAwait(false);
            events.PublishReserved(terminalEvent with
            {
                Payload = terminalEvent.Payload with
                {
                    RunVersion = persisted.Version
                }
            });
            events.EvictPlaintext(work.RunId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            events.EvictPlaintext(work.RunId);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TerminalizeCancelledAsync(work, attempt, policyHash, environment).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Benchmark judge work {RunId} failed.", work.RunId);
            await TerminalizeFailedAsync(work,
                attempt,
                policyHash,
                exception is BenchmarkExecutionException or BenchmarkSnapshotException or LlamaRuntimeException
                    ? exception.Message
                    : InvocationFailedMessage,
                environment).ConfigureAwait(false);
        }
    }

    private RuntimePackage BuildJudgePackage(BenchmarkRuntimeSnapshotV1 snapshot,
        BenchmarkJudgePolicyV1 policy,
        BenchmarkJudgeRuntimeV1 runtime,
        ReadOnlySpan<byte> outputParts)
    {
        var promptPayload = BenchmarkJudgePromptV2.BuildUserPayloadJson(JsonSerializer.Serialize(snapshot.CoreTask),
            policy.ReferenceAnswer,
            policy.Rubric,
            Encoding.UTF8.GetString(outputParts),
            BenchmarkJudgeOutputSchemaV2.Json);
        return packageBuilder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            Guid.NewGuid(),
            BenchmarkJudgePromptV2.SystemPrompt,
            [
                new ConversationMessageDto
                {
                    Id = Guid.NewGuid(),
                    Role = MessageRole.User,
                    Content = promptPayload,
                    SortOrder = 0
                }
            ],
            runtime.Model.ModelName,
            AgentDefinitionVersion: 1,
            ClientNodeId: LocalChatLoopbackDefaults.ClientNodeId,
            AllowedTools: [],
            RequestedCapabilities: [LocalChatLoopbackDefaults.RequestedCapability],
            Timeouts: BenchmarkFrozenPolicies.FrozenTimeouts(),
            SamplingOptions: BenchmarkRunExecutor.ToSamplingOptions(runtime.Sampling, runtime.RequestedContextTokens),
            IsUnattended: true));
    }

    /// <summary>
    ///     Writes the attempt's launch evidence and, in the same insert-if-null write, the rank-cohort key derived from
    ///     it. The key is computed fail-closed: an execution this node cannot fully describe gets no key, and the
    ///     attempt stays permanently unranked rather than joining a cohort it cannot be shown to belong to.
    /// </summary>
    private async Task CheckpointAsync(BenchmarkClaimedWork work,
        BenchmarkJudgeAttemptRecord? attempt,
        string? policyHash,
        LlamaServerLaunchReceipt? receipt,
        RuntimeEnvironmentFactsV1? environment)
    {
        if (attempt is null || policyHash is null)
        {
            return;
        }

        var command = BenchmarkLaunchEvidence.TryBuild(receipt,
            environment,
            attempt.LaunchIntent?.KvCacheTypeSource ?? BenchmarkKvCacheType.SourceAuto);
        if (command is null)
        {
            return;
        }

        try
        {
            _ = await store.MarkJudgeLaunchReadyAsync(attempt.Id,
                               work.QueueSequence,
                               work.Version,
                               command,
                               BenchmarkJudgeExecutionKey.TryCompute(policyHash, receipt, environment),
                               CancellationToken.None)
                           .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Evidence is not worth a measurement. A version race with an operator cancel, or a busy database, must
            // not turn a healthy judging into a failed one — and the token here is None, so nothing caught is a shutdown.
            logger.LogWarning(exception, "Benchmark judge {RunId}: the launch-evidence checkpoint could not be recorded.", work.RunId);
        }
    }

    private async Task TerminalizeCancelledAsync(BenchmarkClaimedWork work,
        BenchmarkJudgeAttemptRecord? attempt,
        string? policyHash,
        RuntimeEnvironmentFactsV1? environment)
    {
        await CheckpointAsync(work, attempt, policyHash, receipt: null, environment).ConfigureAwait(false);
        var runId = work.RunId;
        var run = await store.GetRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
        if (run is null || run.Judge?.State != BenchmarkRunJudgeStates.Running)
        {
            events.EvictPlaintext(runId);
            return;
        }

        var terminal = events.Reserve(runId,
            BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
            new BenchmarkRunStreamPayload(State: BenchmarkRunJudgeStates.Cancelled, RunVersion: run.Version + 1));
        try
        {
            var persisted = await store.MarkJudgeCancelledAsync(runId, work.Version, terminal.Sequence, CancellationToken.None).ConfigureAwait(false);
            events.PublishReserved(terminal with
            {
                Payload = terminal.Payload with
                {
                    RunVersion = persisted.Version
                }
            });
        }
        catch (BenchmarkConflictException)
        {
            var current = await store.GetRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
            if (current?.Judge?.State != BenchmarkRunJudgeStates.Cancelled)
            {
                throw;
            }
        }

        events.EvictPlaintext(runId);
    }

    private async Task TerminalizeFailedAsync(BenchmarkClaimedWork work,
        BenchmarkJudgeAttemptRecord? attempt,
        string? policyHash,
        string message,
        RuntimeEnvironmentFactsV1? environment)
    {
        await CheckpointAsync(work, attempt, policyHash, receipt: null, environment).ConfigureAwait(false);
        var runId = work.RunId;
        var run = await store.GetRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
        if (run is null
            || run.Judge?.State is BenchmarkRunJudgeStates.Succeeded or BenchmarkRunJudgeStates.Failed or BenchmarkRunJudgeStates.Cancelled)
        {
            events.EvictPlaintext(runId);
            return;
        }

        var terminal = events.Reserve(runId,
            BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
            new BenchmarkRunStreamPayload(State: BenchmarkRunJudgeStates.Failed, RunVersion: run.Version + 1));
        var persisted = await store.MarkJudgeFailedAsync(runId, work.Version, message, terminal.Sequence, CancellationToken.None).ConfigureAwait(false);
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
