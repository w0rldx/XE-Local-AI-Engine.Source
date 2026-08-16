namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

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
    private const string InvalidResultMessage = "The benchmark judge returned an invalid result.";

    public async Task ExecuteAsync(BenchmarkClaimedWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (work.Kind != BenchmarkWorkKind.Judge)
        {
            throw new ArgumentException("Judge executor received non-judge work.", nameof(work));
        }

        using var registration = cancellations.Register(work.RunId, BenchmarkWorkKind.Judge, cancellationToken);
        var token = registration.Token;
        RuntimeEnvironmentFactsV1? environment = null;
        try
        {
            events.BeginActivePhase(work.RunId, work.Run.LastStreamSequence);
            var snapshot = snapshots.Deserialize(work.Run.RuntimeSnapshotJson.Span);
            var judge = snapshot.Judge;
            var judgeModel = judge.Enabled && judge.Model is not null && judge.RequestedContextTokens is > 0
                ? judge.Model
                : throw new BenchmarkExecutionException("The frozen judge configuration is invalid.");
            if (work.Run.PrimaryStatus != BenchmarkPrimaryStatus.Succeeded || work.Run.OutputPartsJson is not { } output)
            {
                throw new BenchmarkExecutionException("The primary benchmark result is unavailable for judging.");
            }

            await using var modelLease = await installedModels.AcquireAsync(judgeModel.ModelName, token).ConfigureAwait(false);
            if (!BenchmarkSnapshotModelComparer.Matches(judgeModel, modelLease.Snapshot))
            {
                throw new BenchmarkExecutionException(FingerprintChangedMessage);
            }

            var requiredContext = judge.RequestedContextTokens.Value;
            var runtime = judge.Runtime ?? throw new BenchmarkExecutionException("The frozen judge runtime is unavailable.");

            // The host facts this judging ran on, captured before anything is reserved or spawned. Non-throwing.
            environment = await environmentFacts.CaptureAsync(runtime.Variant, token).ConfigureAwait(false);

            // Admission sizes against the frozen judge runtime's own context, not the project's request.
            // No launch admission — see BenchmarkRunExecutor: the judge spawns its own process from frozen arguments.
            var decision = await capacity.DecideAsync(new CapacityRequest(judgeModel.ModelName,
                                             ModelRole.Chat,
                                             runtime.ContextTokens,
                                             PublishLaunchAdmission: false), token)
                                         .ConfigureAwait(false);
            if (decision.Verdict == CapacityVerdict.RejectInsufficient)
            {
                throw new BenchmarkExecutionException(CapacityRejectedMessage);
            }

            using var reservation = decision.Reservation;
            var package = BuildJudgePackage(snapshot, judgeModel, BenchmarkExecutionSerialization.DeserializeParts(output.Span));
            var admission = new BenchmarkContextAdmissionPolicy(requiredContext);
            using var capture = new BenchmarkInvocationCapture(work.RunId, package.InvocationId, dispatcher, events);
            events.Append(work.RunId,
                BenchmarkRunStreamEventKind.JudgeState,
                new BenchmarkRunStreamPayload(State: BenchmarkJudgeStatus.Running.ToString()));

            var currentVariant = await variantSelector.SelectVariantAsync(token).ConfigureAwait(false);
            if (currentVariant != runtime.Variant)
            {
                throw new BenchmarkExecutionException("The selected judge llama.cpp runtime changed after the benchmark was created.");
            }

            _ = await supervisor.RunExclusiveBenchmarkAsync(judgeModel.ModelName,
                                    ModelRole.Chat,
                                    runtime.ToResolvedLaunchArguments(),
                                    runtime.LaunchPolicy,
                                    async (profiling, profilingToken) =>
                                    {
                                        // Durable BEFORE any token is generated — including on a judge row an
                                        // operator cancellation has already terminalized (S2 successor version).
                                        await CheckpointAsync(work, profiling.LaunchReceipt, environment).ConfigureAwait(false);
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

            var parsed = ParseResult(terminal.StreamedContent,
                judgeModel.ModelContentFingerprint,
                judge.PromptVersion,
                judge.OutputSchemaVersion);
            var terminalEvent = events.Reserve(work.RunId,
                BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
                new BenchmarkRunStreamPayload(State: BenchmarkJudgeStatus.Succeeded.ToString(), RunVersion: work.Run.Version + 1));
            var persisted = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(work.RunId,
                                           work.Version,
                                           BenchmarkExecutionSerialization.SerializeJudge(parsed),
                                           terminalEvent.Sequence), CancellationToken.None)
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
            await TerminalizeCancelledAsync(work, environment).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Benchmark judge work {RunId} failed.", work.RunId);
            await TerminalizeFailedAsync(work,
                exception is BenchmarkExecutionException or LlamaRuntimeException ? exception.Message : InvocationFailedMessage,
                environment).ConfigureAwait(false);
        }
    }

    private RuntimePackage BuildJudgePackage(BenchmarkRuntimeSnapshotV1 snapshot,
        BenchmarkInstalledModelSnapshotV1 judgeModel,
        IReadOnlyList<BenchmarkOutputPart> output)
    {
        var promptPayload = JsonSerializer.Serialize(new
        {
            task = snapshot.CoreTask,
            primaryOutputParts = output,
            outputSchema = snapshot.Judge.OutputSchemaJson
                           ?? throw new BenchmarkExecutionException("The frozen judge output schema is unavailable.")
        });
        return packageBuilder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            Guid.NewGuid(),
            snapshot.Judge.SystemPrompt ?? throw new BenchmarkExecutionException("The frozen judge prompt is unavailable."),
            [
                new ConversationMessageDto
                {
                    Id = Guid.NewGuid(),
                    Role = MessageRole.User,
                    Content = promptPayload,
                    SortOrder = 0
                }
            ],
            judgeModel.ModelName,
            AgentDefinitionVersion: 1,
            ClientNodeId: LocalChatLoopbackDefaults.ClientNodeId,
            AllowedTools: [],
            RequestedCapabilities: [LocalChatLoopbackDefaults.RequestedCapability],
            Timeouts: BenchmarkFrozenPolicies.FrozenTimeouts(),
            SamplingOptions: BenchmarkRunExecutor.ToSamplingOptions(snapshot.Judge.Sampling ?? throw new BenchmarkExecutionException("The frozen judge sampling policy is unavailable."),
                snapshot.Judge.RequestedContextTokens!.Value),
            IsUnattended: true));
    }

    internal static BenchmarkJudgeResultV1 ParseResult(string content,
        string fingerprint,
        int promptVersion,
        int outputSchemaVersion = BenchmarkFrozenPolicies.JudgeOutputSchemaVersion)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException();
            }

            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 3
                || !properties.Select(static property => property.Name).ToHashSet(StringComparer.Ordinal)
                              .SetEquals(["schemaVersion", "score", "rationale"])
                || !root.TryGetProperty("schemaVersion", out var schemaElement)
                || !schemaElement.TryGetInt32(out var schemaVersion)
                || schemaVersion != outputSchemaVersion
                || !root.TryGetProperty("score", out var scoreElement)
                || !scoreElement.TryGetInt32(out var score)
                || score is < 1 or > 5
                || !root.TryGetProperty("rationale", out var rationaleElement)
                || rationaleElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(rationaleElement.GetString()))
            {
                throw new JsonException();
            }

            var rationale = rationaleElement.GetString()!.Trim();
            if (rationale.Length > 8192)
            {
                throw new JsonException();
            }

            return new BenchmarkJudgeResultV1(outputSchemaVersion, score, rationale, fingerprint, promptVersion);
        }
        catch (JsonException exception)
        {
            throw new BenchmarkExecutionException(InvalidResultMessage)
            {
                Source = exception.Source
            };
        }
    }

    /// <summary>
    ///     Writes the judge phase's launch evidence. Insert-if-null and keyed by the work item, so recording it at
    ///     readiness and again while terminalizing keeps the first observation.
    /// </summary>
    private async Task CheckpointAsync(BenchmarkClaimedWork work,
        LlamaServerLaunchReceipt? receipt,
        RuntimeEnvironmentFactsV1? environment)
    {
        var command = BenchmarkLaunchEvidence.TryBuild(receipt,
            environment,
            work.Run.JudgeLaunchIntent?.KvCacheTypeSource ?? BenchmarkKvCacheType.SourceAuto);
        if (command is null)
        {
            return;
        }

        try
        {
            _ = await store.MarkJudgeLaunchReadyAsync(work.RunId, work.QueueSequence, work.Version, command, CancellationToken.None)
                           .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Evidence is not worth a measurement. A version race with an operator cancel, or a busy database, must
            // not turn a healthy run into a failed one — and the token here is None, so nothing caught is a shutdown.
            logger.LogWarning(exception, "Benchmark judge {RunId}: the launch-evidence checkpoint could not be recorded.", work.RunId);
        }
    }

    private async Task TerminalizeCancelledAsync(BenchmarkClaimedWork work, RuntimeEnvironmentFactsV1? environment)
    {
        await CheckpointAsync(work, receipt: null, environment).ConfigureAwait(false);
        var runId = work.RunId;
        var run = await store.GetRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
        if (run is null || run.JudgeStatus == BenchmarkJudgeStatus.Cancelled)
        {
            events.EvictPlaintext(runId);
            return;
        }

        if (run.JudgeStatus != BenchmarkJudgeStatus.Running)
        {
            events.EvictPlaintext(runId);
            return;
        }

        var terminal = events.Reserve(runId,
            BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
            new BenchmarkRunStreamPayload(State: BenchmarkJudgeStatus.Cancelled.ToString(), RunVersion: run.Version + 1));
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
            if (current?.JudgeStatus != BenchmarkJudgeStatus.Cancelled)
            {
                throw;
            }
        }

        events.EvictPlaintext(runId);
    }

    private async Task TerminalizeFailedAsync(BenchmarkClaimedWork work, string message, RuntimeEnvironmentFactsV1? environment)
    {
        await CheckpointAsync(work, receipt: null, environment).ConfigureAwait(false);
        var runId = work.RunId;
        var run = await store.GetRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
        if (run is null || run.JudgeStatus is BenchmarkJudgeStatus.Succeeded or BenchmarkJudgeStatus.Failed or BenchmarkJudgeStatus.Cancelled)
        {
            events.EvictPlaintext(runId);
            return;
        }

        var terminal = events.Reserve(runId,
            BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
            new BenchmarkRunStreamPayload(State: BenchmarkJudgeStatus.Failed.ToString(), RunVersion: run.Version + 1));
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
