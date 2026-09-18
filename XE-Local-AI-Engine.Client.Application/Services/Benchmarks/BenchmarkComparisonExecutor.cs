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

public interface IBenchmarkComparisonExecutor
{
    Task ExecuteAsync(BenchmarkClaimedWork work, CancellationToken cancellationToken);
}

/// <summary>
///     Runs one ORDERED judging of one pair: the two runs' answers side by side, in the presentation order the
///     comparison row names, under the pairwise prompt and its constrained-decoding schema.
/// </summary>
/// <remarks>
///     It is the pointwise judge executor with four differences: the payload carries two answers instead of one and
///     each is bounded to HALF the judge window, the verdict is normalized back to the canonical pair before it is
///     stored, the task-case invariant is re-asserted before anything is leased, and the cohort's fit is re-attempted
///     after every success — a no-op until the last comparison of the cohort lands.
/// </remarks>
public sealed class BenchmarkComparisonExecutor : IBenchmarkComparisonExecutor
{
    private const string FingerprintChangedMessage = "The installed judge model changed after the benchmark was created.";
    private const string CapacityRejectedMessage = "The judge could not reserve enough local model capacity.";
    private const string InvocationFailedMessage = "The benchmark judge invocation failed. See local logs for details.";

    /// <summary>
    ///     Refusal for a comparison whose two runs no longer answer the same question. Cheap, and it closes the window
    ///     the planner's own check cannot: an item edited between enqueue and execution.
    /// </summary>
    internal const string CrossCaseMessage = "The two runs of this comparison no longer answer the same task case.";

    private static readonly JsonElement PairwiseResponseFormatSchema = ParseResponseFormatSchema();
    private readonly IBenchmarkStore _store;
    private readonly IBenchmarkRuntimeSnapshotFactory _snapshots;
    private readonly IBenchmarkInstalledModelLeaseProvider _installedModels;
    private readonly ICapacityService _capacity;
    private readonly ILocalChatRuntimePackageBuilder _packageBuilder;
    private readonly IWorkerEventDispatcher _dispatcher;
    private readonly IInvocationRunner _runner;
    private readonly ILlamaServerProcessSupervisor _supervisor;
    private readonly IGpuVariantSelector _variantSelector;
    private readonly ILlamaServerEndpointBinding _endpointBinding;
    private readonly IBenchmarkEventBuffer _events;
    private readonly IBenchmarkCancellationRegistry _cancellations;
    private readonly IRuntimeEnvironmentFactsProvider _environmentFacts;
    private readonly IBenchmarkPairwiseFitter _fitter;
    private readonly BenchmarkAdmissionRetry _admissionRetry;
    private readonly ILogger<BenchmarkComparisonExecutor> _logger;

    public BenchmarkComparisonExecutor(
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
        IBenchmarkPairwiseFitter fitter,
        BenchmarkAdmissionRetry admissionRetry,
        ILogger<BenchmarkComparisonExecutor> logger)
    {
        _store = store;
        _snapshots = snapshots;
        _installedModels = installedModels;
        _capacity = capacity;
        _packageBuilder = packageBuilder;
        _dispatcher = dispatcher;
        _runner = runner;
        _supervisor = supervisor;
        _variantSelector = variantSelector;
        _endpointBinding = endpointBinding;
        _events = events;
        _cancellations = cancellations;
        _environmentFacts = environmentFacts;
        _fitter = fitter;
        _admissionRetry = admissionRetry;
        _logger = logger;
    }

    private static JsonElement ParseResponseFormatSchema()
    {
        using var document = JsonDocument.Parse(BenchmarkPairwiseOutputSchemaV1.ResponseFormatJson);
        return document.RootElement.Clone();
    }

    public async Task ExecuteAsync(BenchmarkClaimedWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (work.Kind != BenchmarkWorkKind.Comparison)
        {
            throw new ArgumentException("Comparison executor received non-comparison work.", nameof(work));
        }

        if (work.ComparisonId is not { } comparisonId)
        {
            throw new ArgumentException("Comparison work must name the comparison it judges.", nameof(work));
        }

        using var registration = _cancellations.Register(work.RunId, BenchmarkWorkKind.Comparison, cancellationToken);
        var token = registration.Token;
        RuntimeEnvironmentFactsV1? environment = null;
        BenchmarkComparisonRecord? comparison = null;
        string? policyHash = null;
        try
        {
            comparison = await _store.GetComparisonAsync(comparisonId, token)
                         ?? throw new BenchmarkExecutionException("The comparison is no longer available.");
            // A comparison frozen under a different launch-identity scheme is failed before it launches.
            BenchmarkLaunchIdentityScheme.RequireCurrent(comparison.LaunchIntent);
            var revision = await _store.GetJudgePolicyRevisionAsync(comparison.PolicyRevisionId, token)
                           ?? throw new BenchmarkExecutionException("The judge policy revision is no longer available.");
            policyHash = revision.PolicyHash;
            var policy = BenchmarkJudgeSerialization.DeserializePolicy(revision.PolicyJson!.Value.Span);
            if (!BenchmarkJudgePolicyValidator.VersionsAreCurrent(policy))
            {
                throw new BenchmarkExecutionException(BenchmarkJudgeExecutor.OutdatedPolicyVersionMessage);
            }

            if (!string.Equals(BenchmarkJudgePolicyModes.Normalize(policy.Mode), BenchmarkJudgePolicyModes.Pairwise, StringComparison.Ordinal))
            {
                throw new BenchmarkExecutionException("The project no longer judges pairwise.");
            }

            // The legacy single-case invariant, re-asserted before a model is leased. A comparison carrying task-case
            // identity was enqueued against a different shape of the world than this execution path supports.
            if (comparison.TaskCaseId is not null || comparison.TaskInputHash.Length != 0)
            {
                throw new BenchmarkExecutionException(CrossCaseMessage);
            }

            var runtime = comparison.JudgeRuntimeJson is { } runtimeJson
                ? BenchmarkJudgeSerialization.DeserializeRuntime(runtimeJson.Span)
                : throw new BenchmarkExecutionException("The frozen judge runtime is unavailable.");
            var runA = await RequireAnsweredRunAsync(comparison.RunAId, work, token);
            var runB = await RequireAnsweredRunAsync(comparison.RunBId, work, token);
            var snapshot = _snapshots.Deserialize(runA.RuntimeSnapshotJson.Span);

            // HALF the judge window each, because both answers plus the task, the reference answer and the verdict
            // have to fit one context. A long answer is therefore cut harder here than it is pointwise — which is
            // itself a bias, so the cut is RECORDED and a cohort too full of them refuses to aggregate.
            var window = Math.Min(runtime.RequestedContextTokens, runtime.Runtime.ContextTokens) / 2;
            var answerA = BenchmarkOutputParts.ForJudge(BenchmarkExecutionSerialization.DeserializeParts(runA.OutputPartsJson!.Value.Span), window);
            var answerB = BenchmarkOutputParts.ForJudge(BenchmarkExecutionSerialization.DeserializeParts(runB.OutputPartsJson!.Value.Span), window);
            var truncatedA = WasTruncated(answerA);
            var truncatedB = WasTruncated(answerB);

            await using var modelLease = await _installedModels.AcquireAsync(runtime.Model.ModelName, token);
            if (!BenchmarkSnapshotModelComparer.Matches(runtime.Model, modelLease.Snapshot))
            {
                throw new BenchmarkExecutionException(FingerprintChangedMessage);
            }

            environment = await _environmentFacts.CaptureAsync(runtime.Runtime.Variant, token);

            // No launch admission, and a retry around the rejection: a comparison is dequeued by the same single
            // consumer that just ran the previous one, so it routinely arrives while that llama-server is still
            // handing its VRAM back. Wait and re-decide rather than terminalizing a comparison over a transient.
            // ONE budget for the whole phase — see BenchmarkWaitBudget: the capacity wait and the exclusive-spawn
            // wait after it share this allowance rather than each taking a full one.
            var waitBudget = new BenchmarkWaitBudget(_admissionRetry);
            var decision = await BenchmarkCapacityAdmission.AdmitAsync(_capacity,
                                                               new CapacityRequest(runtime.Model.ModelName,
                                                                   ModelRole.Chat,
                                                                   runtime.Runtime.ContextTokens,
                                                                   PublishLaunchAdmission: false,
                                                                   runtime.Runtime.KvTypeK),
                                                               new BenchmarkAdmissionContext(work.RunId,
                                                                   "comparison",
                                                                   runtime.RequestedContextTokens,
                                                                   runtime.Runtime.KvTypeK ?? BenchmarkKvCacheType.F16,
                                                                   CapacityRejectedMessage),
                                                               waitBudget,
                                                               _logger,
                                                               token);
            using var reservation = decision.Reservation;
            var package = BuildComparisonPackage(snapshot, policy, runtime, comparison.Order == 0 ? answerA : answerB,
                comparison.Order == 0 ? answerB : answerA,
                comparison.Order == 0 ? truncatedA : truncatedB,
                comparison.Order == 0 ? truncatedB : truncatedA);
            var admission = new BenchmarkContextAdmissionPolicy(runtime.RequestedContextTokens);

            // The capture is reused verbatim from the judge path, keyed by the work item's run. No active phase is
            // begun for it: a comparison is not a phase of either run, and claiming one would make a run's live view
            // report a judging that is not about it. The buffered deltas are evicted on the way out.
            using var capture = new BenchmarkInvocationCapture(work.RunId, package.InvocationId, _dispatcher, _events);
            var currentVariant = await _variantSelector.SelectVariantAsync(token);
            if (currentVariant != runtime.Runtime.Variant)
            {
                throw new BenchmarkExecutionException("The selected judge llama.cpp runtime changed after the comparison was enqueued.");
            }

            var judged = comparison;
            var judgedPolicyHash = policyHash;
            // A refused pre-spawn eviction is transient — the model is serving a request that ends on its own — so the
            // spawn waits and retries rather than terminalizing this comparison. See BenchmarkExclusiveSpawn.
            _ = await BenchmarkExclusiveSpawn.RunAsync(spawnToken =>
                                                     _supervisor.RunExclusiveBenchmarkAsync(runtime.Model.ModelName,
                                                         ModelRole.Chat,
                                                         runtime.Runtime.ToResolvedLaunchArguments(),
                                                         runtime.Runtime.LaunchPolicy,
                                                         async (profiling, profilingToken) =>
                                                         {
                                                             // Durable BEFORE a token is generated: the receipt, the environment facts and
                                                             // the execution key the fit will insist every fitted comparison shares.
                                                             await CheckpointAsync(work, judged, judgedPolicyHash, profiling.LaunchReceipt, environment);
                                                             using var endpointScope = _endpointBinding.Bind(profiling.Endpoint);
                                                             await using var assignment = await _dispatcher.ReportInvocationAssignedAsync(package, profilingToken);
                                                             using var context = InvocationExecutionContext.CreatePlain(package, Guid.Empty, generationAdmissionPolicy: admission);
                                                             await _runner.RunAsync(context, profilingToken);
                                                             return true;
                                                         },
                                                         spawnToken),
                                                 waitBudget,
                                                 work.RunId,
                                                 "comparison",
                                                 _logger,
                                                 token);
            token.ThrowIfCancellationRequested();
            var terminal = capture.TerminalState;
            if (terminal?.Status != InvocationStatus.Completed)
            {
                throw new BenchmarkExecutionException(InvocationFailedMessage);
            }

            var parsed = BenchmarkPairwiseResultParser.Parse(terminal.StreamedContent);
            await _store.MarkComparisonSucceededAsync(new BenchmarkComparisonSuccessCommand(work.QueueSequence,
                           work.Version,
                           BenchmarkPairwiseResultParser.ToCanonicalVerdict(parsed.Verdict, comparison.Order),
                           new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(BenchmarkCanonicalJson.Serialize(parsed))),
                           truncatedA,
                           truncatedB), CancellationToken.None);
            _events.EvictPlaintext(work.RunId);
            _ = await _fitter.TryPublishAsync(comparison.ProjectId, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _events.EvictPlaintext(work.RunId);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TerminalizeAsync(work, comparison, policyHash, environment, cancelled: true, message: null);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Benchmark comparison {ComparisonId} failed.", comparisonId);
            await TerminalizeAsync(work,
                comparison,
                policyHash,
                environment,
                cancelled: false,
                exception is BenchmarkExecutionException or BenchmarkSnapshotException or LlamaRuntimeException
                    ? exception.Message
                    : InvocationFailedMessage);
        }
    }

    private async Task<BenchmarkRunRecord> RequireAnsweredRunAsync(Guid runId, BenchmarkClaimedWork work, CancellationToken token)
    {
        var run = work.Run.Id == runId ? work.Run : await _store.GetRunAsync(runId, token);
        if (run is null || run.PrimaryStatus != BenchmarkPrimaryStatus.Succeeded || run.OutputPartsJson is null)
        {
            throw new BenchmarkExecutionException("A run of this comparison has no benchmark result to judge.");
        }

        return run;
    }

    /// <summary>
    ///     Whether the graded projection had to be cut. Read off the marker <see cref="BenchmarkOutputParts.ForJudge" />
    ///     appends rather than re-derived from lengths, so the flag and the text the judge saw cannot disagree.
    /// </summary>
    private static bool WasTruncated(IReadOnlyList<BenchmarkOutputPart> graded) =>
        graded.Count > 0 && graded[^1].Content?.EndsWith(BenchmarkOutputParts.TruncationMarker, StringComparison.Ordinal) == true;

    private RuntimePackage BuildComparisonPackage(BenchmarkRuntimeSnapshotV1 snapshot,
        BenchmarkJudgePolicyV1 policy,
        BenchmarkJudgeRuntimeV1 runtime,
        IReadOnlyList<BenchmarkOutputPart> firstAnswer,
        IReadOnlyList<BenchmarkOutputPart> secondAnswer,
        bool firstTruncated,
        bool secondTruncated)
    {
        var promptPayload = BenchmarkPairwisePromptV1.BuildUserPayloadJson(JsonSerializer.Serialize(snapshot.CoreTask),
            policy.ReferenceAnswer,
            Encoding.UTF8.GetString(BenchmarkExecutionSerialization.SerializeParts(firstAnswer)),
            Encoding.UTF8.GetString(BenchmarkExecutionSerialization.SerializeParts(secondAnswer)),
            BenchmarkPairwiseOutputSchemaV1.Json,
            firstTruncated,
            secondTruncated);
        return _packageBuilder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            Guid.NewGuid(),
            BenchmarkPairwisePromptV1.SystemPromptFor(firstTruncated || secondTruncated),
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
            IsUnattended: true,
            ResponseJsonSchema: PairwiseResponseFormatSchema));
    }

    /// <inheritdoc cref="BenchmarkJudgeExecutor" />
    private async Task CheckpointAsync(BenchmarkClaimedWork work,
        BenchmarkComparisonRecord comparison,
        string? policyHash,
        LlamaServerLaunchReceipt? receipt,
        RuntimeEnvironmentFactsV1? environment)
    {
        var command = policyHash is null ? null : BenchmarkLaunchEvidence.TryBuild(receipt, environment, BenchmarkKvCacheType.SourceAuto);
        if (command is null)
        {
            return;
        }

        try
        {
            _ = await _store.MarkComparisonLaunchReadyAsync(comparison.Id,
                               work.QueueSequence,
                               work.Version,
                               command,
                               BenchmarkJudgeExecutionKey.TryCompute(policyHash!, receipt, environment),
                               CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Evidence is not worth a measurement, and the token here is None, so nothing caught is a shutdown.
            _logger.LogWarning(exception, "Benchmark comparison {ComparisonId}: the launch-evidence checkpoint could not be recorded.", comparison.Id);
        }
    }

    private async Task TerminalizeAsync(BenchmarkClaimedWork work,
        BenchmarkComparisonRecord? comparison,
        string? policyHash,
        RuntimeEnvironmentFactsV1? environment,
        bool cancelled,
        string? message)
    {
        if (comparison is not null)
        {
            await CheckpointAsync(work, comparison, policyHash, receipt: null, environment);
        }

        try
        {
            if (cancelled)
            {
                await _store.MarkComparisonCancelledAsync(work.QueueSequence, work.Version, CancellationToken.None);
            }
            else
            {
                await _store.MarkComparisonFailedAsync(work.QueueSequence, work.Version, message ?? InvocationFailedMessage, CancellationToken.None);
            }
        }
        catch (BenchmarkStoreException exception)
        {
            // Already terminal, or claimed at a different version: the queue's own guard fails it closed either way.
            _logger.LogWarning(exception, "Benchmark comparison work {QueueSequence} could not be terminalized.", work.QueueSequence);
        }

        _events.EvictPlaintext(work.RunId);
    }
}
