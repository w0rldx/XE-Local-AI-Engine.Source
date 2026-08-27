namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

public interface IDatasetGenerationExecutor
{
    Task ExecuteAsync(DatasetGenerationClaimedWork work, CancellationToken cancellationToken);
}

/// <summary>
///     Runs one dataset's generation to a terminal state. It owns durable terminalization: the queue's outer guard only
///     ever sees a failure of THIS method's own error handling, never an ordinary generation failure.
/// </summary>
/// <remarks>
///     The run is registered in <see cref="TrainingRunCancellationRegistry" /> under the DATASET id — a dataset id is
///     never a run id, and the evaluation executor already shares the registry the same way. An operator cancel
///     terminalizes as <c>Cancelled</c> and returns normally; only a host shutdown rethrows, because the queue loop
///     reads that as its own stop signal rather than as a failure to log.
/// </remarks>
public sealed class DatasetGenerationExecutor(
    ITrainingDatasetStore store,
    IStructuredAgentRunner runner,
    ISampleValidationPipeline pipeline,
    ILocalModelProviderResolver providerResolver,
    IDatasetGenerationEventBuffer events,
    TrainingRunCancellationRegistry cancellations,
    ILogger<DatasetGenerationExecutor> logger) : IDatasetGenerationExecutor
{
    /// <summary>
    ///     The record schema the teacher is asked for and — crucially — the ORIGINAL schema every generated record is
    ///     validated against. The MEAI adapter rewrites what the teacher actually sees (all-required, bounds folded into
    ///     the description), so only this copy expresses what the definition really requires.
    /// </summary>
    private static readonly JsonElement RecordSchema = JsonDocument.Parse("""
                                                                          {
                                                                            "type": "object",
                                                                            "properties": {
                                                                              "userMessage": { "type": "string" },
                                                                              "assistantText": { "type": "string" },
                                                                              "toolName": { "type": "string" },
                                                                              "toolArgumentsJson": { "type": "string" }
                                                                            },
                                                                            "required": ["userMessage", "assistantText"]
                                                                          }
                                                                          """).RootElement.Clone();

    private readonly TrainingRunCancellationRegistry _cancellations = cancellations ?? throw new ArgumentNullException(nameof(cancellations));
    private readonly IDatasetGenerationEventBuffer _events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly ILogger<DatasetGenerationExecutor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ISampleValidationPipeline _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    private readonly ILocalModelProviderResolver _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
    private readonly IStructuredAgentRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly ITrainingDatasetStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task ExecuteAsync(DatasetGenerationClaimedWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var registration = _cancellations.Register(work.DatasetId, cancellation);
        var generationToken = cancellation.Token;
        try
        {
            await GenerateAsync(work, generationToken).ConfigureAwait(false);
            _ = _events.Append(work.DatasetId, DatasetGenerationEventKind.State, new DatasetGenerationPayload(State: nameof(TrainingDatasetStatus.Ready)));
            _ = await _store.CompleteGenerationAsync(work.DatasetId, DatasetGenerationWorkStatus.Succeeded, errorMessage: null, generationToken)
                            .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (generationToken.IsCancellationRequested)
        {
            _ = await _store.CompleteGenerationAsync(work.DatasetId, DatasetGenerationWorkStatus.Cancelled, errorMessage: null, CancellationToken.None)
                            .ConfigureAwait(false);

            // An operator cancel is an ordinary outcome, already recorded — rethrowing it would make the queue log a
            // "queue failed" error for work that ended exactly as asked. Only a host stop propagates, because that is
            // the loop's own signal to return.
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Dataset generation failed for dataset {DatasetId}.", work.DatasetId);
            var reason = exception is TrainingStoreException ? exception.Message : "Dataset generation failed.";
            _ = _events.Append(work.DatasetId, DatasetGenerationEventKind.State,
                new DatasetGenerationPayload(State: nameof(TrainingDatasetStatus.Failed), Reason: reason));
            _ = await _store.CompleteGenerationAsync(work.DatasetId, DatasetGenerationWorkStatus.Failed, reason, CancellationToken.None)
                            .ConfigureAwait(false);
        }
        finally
        {
            _events.EvictPlaintext(work.DatasetId);
        }
    }

    private async Task GenerateAsync(DatasetGenerationClaimedWork work, CancellationToken cancellationToken)
    {
        // The PINNED body, not the live definition row: an edit between the dataset's creation and this run would
        // otherwise swap the teacher, the tool snapshot or the instructions while the dataset still claims the
        // DefinitionVersion it was created at.
        var definition = DatasetDefinitionService.ReadPinnedBody(work.Dataset)
                         ?? throw new TrainingValidationException(DatasetDefinitionService.UnpinnedDatasetReason);
        var plan = BuildPlan(definition);

        // Checked here as well as in the runner: this is the seam that RESOLVES a provider and builds the client, and
        // reaching it with an ext: id would construct a live connection to the external endpoint before the runner's
        // own guard ever saw the first turn.
        TrainingModelEligibility.EnsureNotExternal(definition.TeacherModelName, "dataset generation teachers");

        var provider = await _providerResolver.ResolveProviderForModelAsync(definition.TeacherModelName, cancellationToken).ConfigureAwait(false);
        // One node-local client for the whole run; IChatClient is IDisposable and this one is ours (never the shared singleton).
        using var teacherClient = provider.CreateChatClient(new LocalModelSelection
        {
            ModelName = definition.TeacherModelName,
            ProviderName = provider.ProviderName
        });
        using var criticClient = await CreateCriticClientAsync(definition, cancellationToken).ConfigureAwait(false);

        var systemInstructions = ComposeSystemInstructions(definition);
        _ = _events.Append(work.DatasetId, DatasetGenerationEventKind.State, new DatasetGenerationPayload(State: nameof(TrainingDatasetStatus.Generating)));

        for (var index = 0; index < plan.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = plan[index];
            var request = new StructuredAgentRequest(definition.TeacherModelName,
                systemInstructions,
                ComposeUserPrompt(target, index),
                definition.TeacherOutputMode,
                RecordSchema,
                definition.Temperature,
                // Per-sample determinism: base seed + sample index, computed as a long and only then formatted back to
                // the string the seed field is carried as.
                OffsetSeed(definition.BaseSeed, index));

            var completion = await _runner.RunAsync(teacherClient, request, cancellationToken).ConfigureAwait(false);
            if (!completion.Success)
            {
                await RejectAsync(work.DatasetId, completion.FailureReason, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var outcome = await _pipeline.ValidateAsync(completion.Text,
                                             new SampleValidationContext(definition, target.Kind, target.Label, RecordSchema, criticClient),
                                             cancellationToken)
                                         .ConfigureAwait(false);
            if (!outcome.Accepted || outcome.Content is null)
            {
                await RejectAsync(work.DatasetId, outcome.RejectionReason, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var contentJson = JsonSerializer.SerializeToUtf8Bytes(outcome.Content, TrainingJson.Options);
            var append = await _store.AppendSampleAsync(new TrainingSampleInput(work.DatasetId,
                                             target.Kind,
                                             outcome.Label,
                                             contentJson,
                                             JsonSerializer.SerializeToUtf8Bytes(outcome.Validation, TrainingJson.Options),
                                             TrainingSampleProvenance.Generated,
                                             SourceHash(contentJson)),
                                         cancellationToken)
                                     .ConfigureAwait(false);

            _ = _events.Append(work.DatasetId,
                append.Duplicate ? DatasetGenerationEventKind.Rejected : DatasetGenerationEventKind.SampleAdded,
                new DatasetGenerationPayload(Completed: index + 1,
                    Total: plan.Count,
                    Kind: target.Kind,
                    Label: outcome.Label.ToString(),
                    Reason: append.Duplicate ? "duplicate" : null));
        }
    }

    private async Task RejectAsync(Guid datasetId, string? reason, CancellationToken cancellationToken)
    {
        await _store.RecordRejectedSampleAsync(datasetId, cancellationToken).ConfigureAwait(false);
        _ = _events.Append(datasetId, DatasetGenerationEventKind.Rejected, new DatasetGenerationPayload(Reason: reason));
        // The hub buffer is transient and evicted when the run terminalizes; the count survives but the reason would
        // not. Log it so a rejection stays diagnosable after the fact (invariant: fail-visible, never fail-silent).
        // Reasons are validator/transport messages, never sample content.
        _logger.LogInformation("Dataset {DatasetId} rejected a generated sample: {Reason}", datasetId, reason ?? "(no reason recorded)");
    }

    private async Task<IChatClient?> CreateCriticClientAsync(DatasetDefinitionBodyV1 definition, CancellationToken cancellationToken)
    {
        if (!definition.CriticEnabled || string.IsNullOrWhiteSpace(definition.CriticModelName))
        {
            return null;
        }

        // The critic never passes through the teacher runner, so this is the ONLY place its model is validated.
        TrainingModelEligibility.EnsureNotExternal(definition.CriticModelName, "dataset generation critics");

        var provider = await _providerResolver.ResolveProviderForModelAsync(definition.CriticModelName, cancellationToken).ConfigureAwait(false);
        return provider.CreateChatClient(new LocalModelSelection
        {
            ModelName = definition.CriticModelName,
            ProviderName = provider.ProviderName
        });
    }

    /// <summary>Flattens the kind targets into a stable per-sample plan; the sample index into it is the seed offset.</summary>
    private static IReadOnlyList<DatasetSampleKindTargetV1> BuildPlan(DatasetDefinitionBodyV1 definition) =>
        definition.SampleKinds.SelectMany(target => Enumerable.Repeat(target, target.Count)).ToArray();

    private static string ComposeSystemInstructions(DatasetDefinitionBodyV1 definition)
    {
        var builder = new StringBuilder(definition.SystemInstructions);
        if (definition.Tools.Count == 0)
        {
            return builder.ToString();
        }

        _ = builder.AppendLine().AppendLine()
                   .AppendLine(
                       "Record convention: for an example where the assistant answers WITHOUT calling a tool, set toolName to an empty string \"\" and toolArgumentsJson to \"\". Only name a tool when the assistant actually calls it, and then put its JSON arguments in toolArgumentsJson.")
                   .AppendLine().AppendLine("Tools available to the assistant in the examples you produce:");
        foreach (var tool in definition.Tools)
        {
            _ = builder.Append("- ").Append(tool.Name);
            if (!string.IsNullOrWhiteSpace(tool.Description))
            {
                _ = builder.Append(": ").Append(tool.Description);
            }

            if (!string.IsNullOrWhiteSpace(tool.ParameterSchema))
            {
                _ = builder.AppendLine().Append("  parameters: ").Append(tool.ParameterSchema);
            }

            _ = builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string ComposeUserPrompt(DatasetSampleKindTargetV1 target, int index) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Produce training example {index + 1} of kind '{target.Kind}'. It must demonstrate {(target.Label == TrainingSampleLabel.Good ? "correct" : "incorrect")} behaviour. Emit only the JSON record.");

    private static string? OffsetSeed(string? baseSeed, int index)
    {
        if (string.IsNullOrWhiteSpace(baseSeed) || !long.TryParse(baseSeed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
        {
            return null;
        }

        // Unchecked on purpose: a base seed near long.MaxValue wraps rather than aborting a run over an arithmetic edge.
        return unchecked(seed + index).ToString(CultureInfo.InvariantCulture);
    }

    private static string SourceHash(ReadOnlySpan<byte> contentJson) =>
        Convert.ToHexStringLower(SHA256.HashData(contentJson));
}
