namespace XE_Local_AI_Engine.Client.Services.Training.Evaluation;

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

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
///         <strong>Exclusivity (plan decision #13): activity yes, runtime-mutation lease NO.</strong> An evaluation
///         holds <c>ITrainingActivity</c> for the whole run, so no training run, dataset generation, benchmark or image
///         job can start beside it — and it cannot start beside one of them. It deliberately does NOT hold the
///         llama.cpp runtime-mutation lease, because that lease exists to keep the runtime binaries still while nothing
///         is loaded: it refuses while an inference process is running, and a model load refuses while it is held. An
///         evaluation's entire job is to LOAD a model and ask it one question per sample, so holding the lease would
///         deadlock it against itself. The queue is what enforces this split — it takes the lease only for the
///         <see cref="TrainingWorkKind.TrainingRun" /> branch.
///     </para>
///     <para>
///         The model is reached through the ordinary node-local chat path (the same
///         <c>ILocalModelProviderResolver</c> seam <c>DatasetGenerationExecutor</c> uses), NOT through an agent: an
///         agent would invoke the tools it was offered. Here the offers are declaration-only
///         (<see cref="DeclaredOnlyAIFunction" />) and the raw client returns the call unexecuted, which is the whole
///         question an evaluation asks — "which call would this model make".
///     </para>
/// </remarks>
public sealed class EvaluationRunExecutor(
    ITrainingEvaluationStore store,
    ITrainingDatasetStore datasets,
    ILocalModelProviderResolver providerResolver,
    ITrainingRunEventBuffer events,
    TrainingRunCancellationRegistry cancellations,
    ILogger<EvaluationRunExecutor> logger) : IEvaluationRunExecutor
{
    private readonly TrainingRunCancellationRegistry _cancellations = cancellations ?? throw new ArgumentNullException(nameof(cancellations));
    private readonly ITrainingDatasetStore _datasets = datasets ?? throw new ArgumentNullException(nameof(datasets));
    private readonly ITrainingRunEventBuffer _events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly ILogger<EvaluationRunExecutor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ILocalModelProviderResolver _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
    private readonly ITrainingEvaluationStore _store = store ?? throw new ArgumentNullException(nameof(store));

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

        var provider = await _providerResolver.ResolveProviderForModelAsync(running.ModelName, cancellationToken).ConfigureAwait(false);
        // One node-local client for the whole evaluation; IChatClient is IDisposable and this one is ours.
        using var chatClient = provider.CreateChatClient(new LocalModelSelection
        {
            ModelName = running.ModelName,
            ProviderName = provider.ProviderName
        });

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
            // The membership named a sample the dataset no longer has. That is a real miss, not a reason to abandon
            // the run: recording it keeps ScoredCount reaching TotalCount, which is what the resume cursor bounds on.
            return new TrainingEvaluationResultEntry(sampleId, "unknown", Passed: false, EvaluationScorer.Deterministic,
                "The hold-out sample is no longer in the dataset.");
        }

        var content = Read<TrainingSampleContentV1>(sample.ContentJson);
        if (content is null)
        {
            return new TrainingEvaluationResultEntry(sampleId, sample.Kind, Passed: false, EvaluationScorer.Deterministic,
                "The hold-out sample's frozen trajectory could not be read.");
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

        var options = new ChatOptions
        {
            ModelId = modelName,
            // Temperature 0 plus the definition's own seed: a re-run of the same evaluation has to reach the same
            // verdicts, or a comparison is measuring sampling noise rather than the tuning.
            Temperature = 0f,
            Tools = context.Offers
        };
        if (TryParseSeed(context.Seed, out var seed))
        {
            options.Seed = seed;
        }

        ChatResponse response;
        // Same per-turn bound the teacher runner carries: a completion that never returns must be this sample's
        // verdict, not a queue wedge that also blocks every training run behind it.
        using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        turnCancellation.CancelAfter(StructuredAgentRunner.TurnTimeout);
        try
        {
            response = await chatClient.GetResponseAsync(messages, options, turnCancellation.Token).ConfigureAwait(false);
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
                $"The model could not be reached for this sample: {exception.Message}");
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
        var definitionRecord = await _datasets.GetDefinitionAsync(dataset.DefinitionId, cancellationToken).ConfigureAwait(false)
                               ?? throw new EvaluationRejectedException("The dataset definition no longer exists.");
        var definition = DatasetDefinitionService.ReadBody(definitionRecord.DefinitionJson);

        var wanted = membership.HoldoutSampleIds.ToHashSet();
        var samples = await _datasets.ListAllSamplesAsync(evaluation.DatasetId, cancellationToken).ConfigureAwait(false);
        var byId = samples.Where(sample => wanted.Contains(sample.Id)).ToDictionary(sample => sample.Id);

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
