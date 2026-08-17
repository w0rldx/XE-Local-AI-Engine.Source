namespace XE_Local_AI_Engine.Client.Services.Training.Evaluation;

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public interface IEvaluationRunExecutor
{
    Task ExecuteAsync(TrainingWorkClaim claim, CancellationToken stoppingToken);
}

/// <summary>
///     Drives one claimed evaluation: score every frozen hold-out sample that does not already carry a verdict, then
///     terminalize. One durable write per sample, so an interruption keeps the prefix and a resume continues from the
///     next unscored sample rather than paying for the whole hold-out set again.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Exclusivity:</strong> an evaluation holds <c>ITrainingActivity</c> for the whole run, so no training
///         run, dataset generation, benchmark or image job can start beside it. The executor does not separately take
///         runtime-mutation or model-load admission; <c>ITransientLlamaServerEvaluationHarness</c> owns those leases in
///         launch-safe order. An installed base also keeps its coordinated read snapshot through harness teardown, so
///         replacement cannot race the path-addressed load.
///     </para>
///     <para>
///         <strong>Scoring reads the run-owned immutable corpus, never live sample rows.</strong> The corpus digest,
///         freeze id and stable sample ids are checked before the first model call. A live review edit after the
///         evaluation was queued therefore cannot change the question being scored.
///     </para>
///     <para>
///         Installed-base and staged-tuned evaluations both run through
///         <c>ITransientLlamaServerEvaluationHarness</c> with the same frozen context and launch policy. Model bytes,
///         runtime provenance and teardown are bound before the result becomes quality evidence. Neither path uses an agent: an agent would invoke the tools it was
///         offered. Here the offers are declaration-only
///         (<see cref="DeclaredOnlyAIFunction" />) and the raw client returns the call unexecuted, which is the whole
///         question an evaluation asks — "which call would this model make".
///     </para>
/// </remarks>
public sealed class EvaluationRunExecutor(
    ITrainingEvaluationStore store,
    ITrainingRunStore runs,
    ITrainingDatasetStore datasets,
    TrainingRunWorkspace workspace,
    ITransientLlamaServerEvaluationHarness evaluationHarness,
    IInferenceChatClientFactory chatClientFactory,
    ITrainingEvaluationInstalledModelLeaseProvider installedModels,
    ITrainingRunEventBuffer events,
    TrainingRunCancellationRegistry cancellations,
    ILogger<EvaluationRunExecutor> logger) : IEvaluationRunExecutor
{
    private readonly TrainingRunCancellationRegistry _cancellations = cancellations ?? throw new ArgumentNullException(nameof(cancellations));
    private readonly ITrainingDatasetStore _datasets = datasets ?? throw new ArgumentNullException(nameof(datasets));
    private readonly ITrainingRunEventBuffer _events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly ILogger<EvaluationRunExecutor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private const int EvaluationContextTokens = 4096;
    private readonly ITransientLlamaServerEvaluationHarness _evaluationHarness =
        evaluationHarness ?? throw new ArgumentNullException(nameof(evaluationHarness));
    private readonly IInferenceChatClientFactory _chatClientFactory = chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));
    private readonly ITrainingEvaluationInstalledModelLeaseProvider _installedModels =
        installedModels ?? throw new ArgumentNullException(nameof(installedModels));
    private readonly ITrainingRunStore _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    private readonly ITrainingEvaluationStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TrainingRunWorkspace _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

    public async Task ExecuteAsync(TrainingWorkClaim claim, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var evaluation = await _store.GetAsync(claim.TargetId, stoppingToken).ConfigureAwait(false);
        if (evaluation is null)
        {
            // The row and its work item are created and deleted in one transaction, so this only happens if the
            // database was edited from outside. Startup recovery terminalizes the stranded item.
            _logger.LogError("The claimed evaluation {EvaluationId} has no row; the work item is left for startup recovery.", claim.TargetId);
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        // The registry is keyed by target id and an evaluation id is never a run id, so the run registry carries both
        // rather than standing up a second identical dictionary.
        using var registration = _cancellations.Register(evaluation.Id, cancellation);
        try
        {
            await ScoreAsync(evaluation, cancellation.Token).ConfigureAwait(false);
            await TerminalizeAsync(evaluation, TrainingWorkStatus.Succeeded, message: null).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TerminalizeAsync(evaluation, TrainingWorkStatus.Cancelled, "The evaluation run was cancelled.").ConfigureAwait(false);
        }
        catch (EvaluationRejectedException exception)
        {
            await TerminalizeAsync(evaluation, TrainingWorkStatus.Failed, exception.Message).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The evaluation run {EvaluationId} failed before it could report its own outcome.", evaluation.Id);
            await TerminalizeAsync(evaluation, TrainingWorkStatus.Failed, "The evaluation run failed.").ConfigureAwait(false);
        }
    }

    private async Task ScoreAsync(TrainingEvaluationRecord evaluation, CancellationToken cancellationToken)
    {
        var membership = Read<TrainingEvaluationMembershipV1>(evaluation.MembershipJson)
                         ?? throw new EvaluationRejectedException("The evaluation's frozen membership could not be read.");
        var context = await LoadAsync(evaluation, membership, cancellationToken).ConfigureAwait(false);

        var running = evaluation.Status == TrainingEvaluationStatus.Running
            ? evaluation
            : await _store.TransitionAsync(evaluation.Id, evaluation.Version, TrainingEvaluationStatus.Running, cancellationToken).ConfigureAwait(false);
        Publish(running, TrainingRunEventKind.EvaluationState);

        // The resume cursor: whatever a previous attempt already scored is skipped, so re-entering after an
        // interruption continues at the next unscored sample instead of re-running the whole hold-out set.
        var scored = TrainingEvaluationResults.Read(running.ResultsJson).Select(entry => entry.SampleId).ToHashSet();

        await using var target = await ResolveExecutionTargetAsync(running, cancellationToken).ConfigureAwait(false);
        var request = new TransientLlamaServerEvaluationRequest(target.ModelPath,
            target.AdapterPath,
            EvaluationContextTokens,
            TimeSpan.FromMinutes(10),
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1)
        {
            TeardownTimeout = TimeSpan.FromSeconds(5)
        };
        var result = await _evaluationHarness.RunAsync(request,
            async (provisional, _) =>
            {
                var validated = ValidateLaunchEvidence(provisional.Model, provisional.Launch, target);
                await _store.BindExecutionProvenanceAsync(running.Id,
                        JsonSerializer.SerializeToUtf8Bytes(validated, TrainingJson.Options),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            },
            async (session, token) =>
            {
                using var client = _chatClientFactory.CreateChatClient(session.BaseAddress, session.ModelId);
                await ScoreWithClientAsync(running, membership, context, scored, client, token).ConfigureAwait(false);
                return session;
            }, cancellationToken).ConfigureAwait(false);
        _ = ValidateExecutionEvidence(result, target);
    }

    private async Task<EvaluationExecutionTarget> ResolveExecutionTargetAsync(TrainingEvaluationRecord evaluation,
        CancellationToken cancellationToken)
    {
        if (evaluation.TargetKind == EvaluationModelTargetKind.InstalledModel)
        {
            var lease = await AcquireInstalledAsync(evaluation.ModelName,
                evaluation.ModelContentFingerprint,
                cancellationToken).ConfigureAwait(false);
            return new EvaluationExecutionTarget(lease.ModelFilePath,
                AdapterPath: null,
                ArtifactSha256: null,
                ArtifactSizeBytes: null,
                lease.ModelSha256,
                lease.ModelSizeBytes,
                lease);
        }

        var artifact = evaluation.SourceArtifactId is { } artifactId
            ? await _runs.GetArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false)
            : null;
        if (artifact is null || artifact.DiscardedAtUtc is not null
            || !string.Equals(artifact.Sha256, evaluation.ModelContentFingerprint, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(artifact.Path))
        {
            throw new EvaluationRejectedException("The staged evaluation target no longer matches its recorded artifact identity.");
        }
        var artifactSha256 = artifact.Sha256
                             ?? throw new EvaluationRejectedException("The staged evaluation target has no recorded content identity.");

        await using (var stream = File.OpenRead(artifact.Path))
        {
            var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(digest, artifactSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new EvaluationRejectedException("The staged evaluation target changed after evaluation creation.");
            }
        }

        string modelPath;
        string? adapterPath;
        if (artifact.Kind == TrainingArtifactKind.AdapterGguf)
        {
            var run = await _runs.GetAsync(artifact.RunId, cancellationToken).ConfigureAwait(false)
                      ?? throw new EvaluationRejectedException("The training run behind the staged artifact no longer exists.");
            var baseName = run.LinkedInstalledModelName
                           ?? throw new EvaluationRejectedException("The staged adapter has no installed base counterpart.");
            var lease = await AcquireInstalledAsync(baseName, run.LinkedModelContentFingerprint, cancellationToken).ConfigureAwait(false);
            modelPath = lease.ModelFilePath;
            adapterPath = artifact.Path;
            return new EvaluationExecutionTarget(modelPath,
                adapterPath,
                artifactSha256,
                artifact.SizeBytes,
                lease.ModelSha256,
                lease.ModelSizeBytes,
                lease);
        }
        else
        {
            modelPath = artifact.Path;
            adapterPath = null;
        }

        return new EvaluationExecutionTarget(modelPath,
            adapterPath,
            artifactSha256,
            artifact.SizeBytes,
            ExpectedModelSha256: artifactSha256,
            ExpectedModelSizeBytes: artifact.SizeBytes,
            InstalledLease: null);
    }

    private static TrainingEvaluationExecutionProvenanceV1 ValidateExecutionEvidence(
        TransientLlamaServerEvaluationResult<TransientLlamaServerEvaluationSession> result,
        EvaluationExecutionTarget target)
    {
        var session = result.Value;
        var sessionProjection = session.Launch.LaunchProjection.ComputeIdentity();
        var resultProjection = result.Launch.LaunchProjection.ComputeIdentity();
        if (session.Model != result.Model
            || session.Launch.Variant != result.Launch.Variant
            || !string.Equals(session.Launch.ExecutableVersion, result.Launch.ExecutableVersion, StringComparison.Ordinal)
            || !string.Equals(session.Launch.ExecutableSha256, result.Launch.ExecutableSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(session.Launch.ManifestSha256, result.Launch.ManifestSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(sessionProjection, resultProjection, StringComparison.Ordinal)
            || session.Launch.BenchmarkLaunchPolicy != result.Launch.BenchmarkLaunchPolicy
            || result.Launch.ReceiptVersion != LlamaServerLaunchReceipt.CurrentVersion
            || result.Launch.BenchmarkLaunchPolicy != LlamaServerBenchmarkLaunchPolicy.DeterministicV1
            || result.Launch.EffectiveContextTokens != EvaluationContextTokens
            || string.IsNullOrWhiteSpace(result.Launch.ExecutableVersion)
            || string.IsNullOrWhiteSpace(result.Launch.ExecutableSha256)
            || string.IsNullOrWhiteSpace(result.Launch.ManifestSha256)
            || !string.Equals(result.Launch.ExecutableSha256, result.Launch.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new EvaluationRejectedException("The transient evaluation runtime returned incomplete or mismatched launch provenance.");
        }

        if (!result.Teardown.TreeKillRequested || !result.Teardown.ProcessExitObserved || result.Teardown.ExitObservationTimedOut
            || !result.Teardown.HandleDisposed)
        {
            throw new EvaluationRejectedException("The transient evaluation runtime did not provide complete teardown evidence.");
        }

        if (target.ArtifactSha256 is { } artifactSha256)
        {
            var actualSha256 = target.AdapterPath is null ? result.Model.ModelSha256 : result.Model.AdapterSha256;
            var actualSize = target.AdapterPath is null ? result.Model.ModelSizeBytes : result.Model.AdapterSizeBytes;
            if (!string.Equals(actualSha256, artifactSha256, StringComparison.OrdinalIgnoreCase)
                || actualSize != target.ArtifactSizeBytes)
            {
                throw new EvaluationRejectedException("The transient evaluation target does not match the staged artifact identity.");
            }
        }

        if (!string.Equals(result.Model.ModelSha256, target.ExpectedModelSha256, StringComparison.OrdinalIgnoreCase)
            || result.Model.ModelSizeBytes != target.ExpectedModelSizeBytes)
        {
            throw new EvaluationRejectedException("The transient evaluation base model does not match the coordinated installed identity.");
        }

        return ValidateLaunchEvidence(result.Model, result.Launch, target);
    }

    private static TrainingEvaluationExecutionProvenanceV1 ValidateLaunchEvidence(
        TransientLlamaServerModelProvenance model,
        LlamaServerLaunchReceipt launch,
        EvaluationExecutionTarget target)
    {
        var projectionIdentity = launch.LaunchProjection.ComputeIdentity();
        if (launch.ReceiptVersion != LlamaServerLaunchReceipt.CurrentVersion
            || launch.BenchmarkLaunchPolicy != LlamaServerBenchmarkLaunchPolicy.DeterministicV1
            || launch.EffectiveContextTokens != EvaluationContextTokens
            || string.IsNullOrWhiteSpace(launch.ExecutableVersion)
            || string.IsNullOrWhiteSpace(launch.ExecutableSha256)
            || string.IsNullOrWhiteSpace(launch.ManifestSha256)
            || !string.Equals(launch.ExecutableSha256, launch.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new EvaluationRejectedException("The transient evaluation runtime returned incomplete or mismatched launch provenance.");
        }

        if (target.ArtifactSha256 is { } artifactSha256)
        {
            var actualSha256 = target.AdapterPath is null ? model.ModelSha256 : model.AdapterSha256;
            var actualSize = target.AdapterPath is null ? model.ModelSizeBytes : model.AdapterSizeBytes;
            if (!string.Equals(actualSha256, artifactSha256, StringComparison.OrdinalIgnoreCase)
                || actualSize != target.ArtifactSizeBytes)
            {
                throw new EvaluationRejectedException("The transient evaluation target does not match the staged artifact identity.");
            }
        }

        if (!string.Equals(model.ModelSha256, target.ExpectedModelSha256, StringComparison.OrdinalIgnoreCase)
            || model.ModelSizeBytes != target.ExpectedModelSizeBytes)
        {
            throw new EvaluationRejectedException("The transient evaluation base model does not match the coordinated installed identity.");
        }

        var policy = launch.BenchmarkLaunchPolicy;
        return new TrainingEvaluationExecutionProvenanceV1
        {
            Variant = launch.Variant.ToString(),
            ExecutableVersion = launch.ExecutableVersion,
            ExecutableSha256 = launch.ExecutableSha256,
            ManifestSha256 = launch.ManifestSha256,
            LaunchProjectionIdentity = projectionIdentity,
            ContextTokens = EvaluationContextTokens,
            LaunchPolicyVersion = policy.Version,
            LaunchPolicyChatCacheReuse = policy.ChatCacheReuse,
            LaunchPolicyChatCacheRamMiB = policy.ChatCacheRamMiB,
            LaunchPolicySpeculativeDecoding = policy.SpeculativeDecodingEnabled,
            ModelSha256 = model.ModelSha256,
            ModelSizeBytes = model.ModelSizeBytes,
            AdapterSha256 = model.AdapterSha256,
            AdapterSizeBytes = model.AdapterSizeBytes
        };
    }

    private async Task<ITrainingEvaluationInstalledModelLease> AcquireInstalledAsync(string modelName,
        string? expectedFingerprint,
        CancellationToken cancellationToken)
    {
        ITrainingEvaluationInstalledModelLease lease;
        try
        {
            lease = await _installedModels.AcquireAsync(modelName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            throw new EvaluationRejectedException("The exact installed model identity recorded for this evaluation is no longer available.");
        }

        if (string.IsNullOrWhiteSpace(expectedFingerprint)
            || !string.Equals(lease.ModelContentFingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw new EvaluationRejectedException("The exact installed model identity recorded for this evaluation is no longer available.");
        }

        return lease;
    }

    private sealed record EvaluationExecutionTarget(string ModelPath,
        string? AdapterPath,
        string? ArtifactSha256,
        long? ArtifactSizeBytes,
        string ExpectedModelSha256,
        long ExpectedModelSizeBytes,
        ITrainingEvaluationInstalledModelLease? InstalledLease) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => InstalledLease?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private async Task ScoreWithClientAsync(TrainingEvaluationRecord running,
        TrainingEvaluationMembershipV1 membership,
        EvaluationContext context,
        HashSet<Guid> scored,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {

        foreach (var sampleId in membership.HoldoutSampleIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!scored.Add(sampleId))
            {
                continue;
            }

            var entry = await ScoreSampleAsync(chatClient, running.ModelName, sampleId, context, cancellationToken).ConfigureAwait(false);
            // Durable per sample rather than per batch: an interruption between two samples must cost at most the one
            // that was in flight.
            var latest = await _store.AppendResultsAsync(running.Id, [entry], CancellationToken.None).ConfigureAwait(false);
            Publish(latest, TrainingRunEventKind.EvaluationProgress);
        }
    }

    private static async Task<TrainingEvaluationResultEntry> ScoreSampleAsync(IChatClient chatClient,
        string modelName,
        Guid sampleId,
        EvaluationContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Samples.TryGetValue(sampleId, out var sample))
        {
            // Defensive fallback for a malformed legacy corpus. New freezes validate complete membership before the
            // loop, but preserving a verdict here keeps an old resume cursor able to reach TotalCount.
            return new TrainingEvaluationResultEntry(sampleId, "unknown", Passed: false, EvaluationScorer.Deterministic,
                "The hold-out sample is not present in the frozen corpus.");
        }

        var content = Read<TrainingSampleContentV1>(sample.ContentJson);
        if (content is null)
        {
            return new TrainingEvaluationResultEntry(sampleId, sample.Kind, Passed: false, EvaluationScorer.Deterministic,
                "The hold-out sample's frozen trajectory could not be read.");
        }

        if (EvaluationScorer.RejectMultiCall(sampleId, sample.Kind, content) is { } unsupported)
        {
            return unsupported;
        }

        var prompt = EvaluationScorer.ReadUserPrompt(content);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new TrainingEvaluationResultEntry(sampleId, sample.Kind, Passed: false, EvaluationScorer.Deterministic,
                "The hold-out sample carries no user turn to replay.");
        }

        var expectation = EvaluationScorer.ReadExpectation(content, context.Tools);
        List<ChatMessage> messages =
        [
            new(ChatRole.System, string.IsNullOrWhiteSpace(content.SystemInstructions) ? context.SystemInstructions : content.SystemInstructions),
            new(ChatRole.User, prompt)
        ];

        // Temperature 0 plus the definition's own seed: a re-run of the same evaluation has to reach the same
        // verdicts, or a comparison is measuring sampling noise rather than the tuning.
        var options = TrainingAiClientPolicy.CreateOptions(modelName, temperature: 0f, context.Offers);
        if (TryParseSeed(context.Seed, out var seed))
        {
            options.Seed = seed;
        }

        ChatResponse response;
        // Same per-turn bound the teacher runner carries: a completion that never returns must be this sample's
        // verdict, not a queue wedge that also blocks every training run behind it.
        using var activity = TrainingAiClientPolicy.StartActivity("evaluation");
        using var turnCancellation = TrainingAiClientPolicy.CreateTurnCancellation(cancellationToken);
        try
        {
            response = await chatClient.GetResponseAsync(messages, options, turnCancellation.Token).ConfigureAwait(false);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TrainingEvaluationResultEntry(sampleId, sample.Kind, Passed: false, EvaluationScorer.Deterministic,
                $"The model did not answer within {StructuredAgentRunner.TurnTimeout.TotalMinutes:0} minutes.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One sample's transport or model failure is that sample's verdict, never the evaluation's.
            return new TrainingEvaluationResultEntry(sampleId, sample.Kind, Passed: false, EvaluationScorer.Deterministic,
                TrainingAiClientPolicy.TranslateProviderFailure(activity, exception));
        }

        var calls = response.Messages
                            .SelectMany(message => message.Contents)
                            .OfType<FunctionCallContent>()
                            .Select(call => new EvaluationToolCall(call.Name, SerializeArguments(call.Arguments)))
                            .ToArray();
        return EvaluationScorer.Score(sampleId, sample.Kind, expectation, calls);
    }

    private async Task<EvaluationContext> LoadAsync(TrainingEvaluationRecord evaluation,
        TrainingEvaluationMembershipV1 membership,
        CancellationToken cancellationToken)
    {
        var dataset = await _datasets.GetDatasetAsync(evaluation.DatasetId, cancellationToken).ConfigureAwait(false)
                      ?? throw new EvaluationRejectedException("The evaluated dataset no longer exists.");

        // The body the dataset PINNED at creation, which is also what generation ran against. Reading the live
        // definition would score the model against tools and instructions this dataset never demonstrated.
        var definition = DatasetDefinitionService.ReadPinnedBody(dataset)
                         ?? throw new EvaluationRejectedException(DatasetDefinitionService.UnpinnedDatasetReason);

        var run = await _runs.GetAsync(membership.TrainingRunId, cancellationToken).ConfigureAwait(false)
                  ?? throw new EvaluationRejectedException("The training run behind the frozen corpus no longer exists.");
        var freeze = Read<TrainingRunFreezeV1>(run.FreezeJson)
                     ?? throw new EvaluationRejectedException("The training run's frozen corpus metadata could not be read.");
        if (freeze.FreezeId != membership.FreezeId)
        {
            throw new EvaluationRejectedException("The evaluation membership does not name the training run's frozen corpus.");
        }

        if (!string.Equals(run.DatasetContentFingerprint, membership.DatasetContentFingerprint, StringComparison.Ordinal)
            || !string.Equals(freeze.DatasetContentFingerprint, membership.DatasetContentFingerprint, StringComparison.Ordinal))
        {
            throw new EvaluationRejectedException("The evaluation membership does not match the training run's frozen corpus version.");
        }

        var plaintext = await _workspace.ReadFrozenDatasetAsync(evaluation.DatasetId, freeze.FreezeId, cancellationToken).ConfigureAwait(false);
        var digest = Convert.ToHexStringLower(SHA256.HashData(plaintext.Span));
        if (!string.Equals(digest, freeze.FrozenCopySha256, StringComparison.Ordinal))
        {
            throw new EvaluationRejectedException("The immutable training corpus failed its integrity check.");
        }

        IReadOnlyList<TrainingSampleRecord> frozenSamples;
        try
        {
            frozenSamples = FrozenTrainingCorpus.Read(plaintext.Span, freeze);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            throw new EvaluationRejectedException("The immutable training corpus could not be read.", exception);
        }

        var wanted = membership.HoldoutSampleIds.ToHashSet();
        var byId = frozenSamples.Where(sample => wanted.Contains(sample.Id)).ToDictionary(sample => sample.Id);
        if (byId.Count != wanted.Count)
        {
            throw new EvaluationRejectedException("The immutable training corpus does not contain every frozen hold-out sample.");
        }

        // The SAME snapshot the samples were generated against, declaration-only. Offering the live catalog instead
        // would score the model against tools the dataset never demonstrated.
        var offers = definition.Tools
                               .Select(tool => (AITool)new DeclaredOnlyAIFunction(tool.Name, tool.Description, tool.ParameterSchema))
                               .ToList();
        return new EvaluationContext(byId, definition.Tools, offers, definition.SystemInstructions, definition.BaseSeed);
    }

    private async Task TerminalizeAsync(TrainingEvaluationRecord evaluation, TrainingWorkStatus status, string? message)
    {
        var completed = await _store.CompleteAsync(evaluation.Id, status, message, CancellationToken.None).ConfigureAwait(false);
        Publish(completed, TrainingRunEventKind.EvaluationState, message);
    }

    /// <summary>
    ///     Evaluation progress rides the run's own hub group rather than a group of its own: an evaluation is created
    ///     FROM a run, the operator is already subscribed to that run, and the event kind is what tells the two streams
    ///     apart. An evaluation with no run behind it simply publishes nothing.
    /// </summary>
    private void Publish(TrainingEvaluationRecord evaluation, TrainingRunEventKind kind, string? message = null)
    {
        if (evaluation.TrainingRunId is not { } runId)
        {
            return;
        }

        _ = _events.Append(runId,
            kind,
            new TrainingRunPayload(State: evaluation.Status.ToString(),
                Step: evaluation.ScoredCount,
                TotalSteps: evaluation.TotalCount,
                Message: message,
                EvaluationId: evaluation.Id,
                PassedCount: evaluation.PassedCount));
    }

    private static string SerializeArguments(IDictionary<string, object?>? arguments) =>
        arguments is null or { Count: 0 } ? "{}" : JsonSerializer.Serialize(arguments, TrainingJson.Options);

    private static bool TryParseSeed(string? seed, out long value)
    {
        // -1 is the runtime's "random seed" sentinel; anything below it is invalid, so it is skipped rather than sent.
        value = 0;
        return !string.IsNullOrWhiteSpace(seed)
               && long.TryParse(seed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
               && value >= -1;
    }

    private static T? Read<T>(ReadOnlyMemory<byte>? payload)
        where T : class
    {
        if (payload is not { } bytes || bytes.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(bytes.Span, TrainingJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Everything the scoring loop reads once and then reuses for every sample.</summary>
    private sealed record EvaluationContext(
        IReadOnlyDictionary<Guid, TrainingSampleRecord> Samples,
        IReadOnlyList<DatasetToolSnapshotV1> Tools,
        IList<AITool> Offers,
        string SystemInstructions,
        string? Seed);
}
