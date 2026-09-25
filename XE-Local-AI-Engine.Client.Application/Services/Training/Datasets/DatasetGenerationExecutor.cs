namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Chat;
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
public sealed class DatasetGenerationExecutor : IDatasetGenerationExecutor
{
    /// <summary>
    ///     The record schema the teacher is asked for and — crucially — the ORIGINAL schema every generated record is
    ///     validated against.
    /// </summary>
    /// <remarks>
    ///     The MEAI adapter rewrites what the teacher actually sees (all-required, bounds folded into the
    ///     description), so only this copy expresses what the definition really requires.
    /// </remarks>
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

    /// <summary>The per-sample teacher instruction: {0} = 1-based sample number, {1} = kind, {2} = "correct"/"incorrect".</summary>
    private const string TeacherPromptTemplate =
        "Produce training example {0} of kind '{1}'. It must demonstrate {2} behaviour. Emit only the JSON record.";

    private const int ReportedRejectionReasons = 3;

    private static readonly CompositeFormat TeacherPromptFormat = CompositeFormat.Parse(TeacherPromptTemplate);

    /// <summary>
    ///     Matches a user turn that restates the teacher instruction, built from the template's first sentence so a
    ///     reworded prompt moves the check with it. Live-found: a 7B teacher copied the instruction in as the "user" turn.
    /// </summary>
    private static readonly Regex TeacherPromptEcho = new("^" + string.Join(".+?", TeacherPromptTemplate[..(TeacherPromptTemplate.IndexOf(". ", StringComparison.Ordinal) + 1)]
                                                                                   .Split(["{0}", "{1}", "{2}"], StringSplitOptions.None)
                                                                                   .Select(Regex.Escape)),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly TrainingRunCancellationRegistry _cancellations;
    private readonly IDatasetGenerationEventBuffer _events;
    private readonly ILogger<DatasetGenerationExecutor> _logger;
    private readonly ISampleValidationPipeline _pipeline;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly IStructuredAgentRunner _runner;
    private readonly ITrainingDatasetStore _store;

    public DatasetGenerationExecutor(ITrainingDatasetStore store,
        IStructuredAgentRunner runner,
        ISampleValidationPipeline pipeline,
        ILocalModelProviderResolver providerResolver,
        IDatasetGenerationEventBuffer events,
        TrainingRunCancellationRegistry cancellations,
        ILogger<DatasetGenerationExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(cancellations);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(providerResolver);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(store);
        _cancellations = cancellations;
        _events = events;
        _logger = logger;
        _pipeline = pipeline;
        _providerResolver = providerResolver;
        _runner = runner;
        _store = store;
    }

    public async Task ExecuteAsync(DatasetGenerationClaimedWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var registration = _cancellations.Register(work.DatasetId, cancellation);
        var generationToken = cancellation.Token;
        try
        {
            var tally = await GenerateAsync(work, generationToken);
            if (tally.Accepted == 0)
            {
                // Live-found: a run that rejected every sample read Ready/Succeeded with zero samples.
                var failure = NoUsableSamplesReason(tally);
                _logger.LogWarning("Dataset {DatasetId} produced no usable samples: {Reason}", work.DatasetId, failure);
                await CommitThenPublishAsync(work.DatasetId, DatasetGenerationWorkStatus.Failed, TrainingDatasetStatus.Failed, failure, generationToken);
                return;
            }

            await CommitThenPublishAsync(work.DatasetId, DatasetGenerationWorkStatus.Succeeded, TrainingDatasetStatus.Ready, reason: null, generationToken);
        }
        catch (OperationCanceledException) when (generationToken.IsCancellationRequested)
        {
            _ = await _store.CompleteGenerationAsync(work.DatasetId, DatasetGenerationWorkStatus.Cancelled, errorMessage: null, CancellationToken.None);

            // An operator cancel is an ordinary outcome, already recorded: rethrowing it would make the queue log a
            // "queue failed" error for work that ended exactly as asked. Only a host stop propagates — the loop's own signal.
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
                new DatasetGenerationPayload
                {
                    State = nameof(TrainingDatasetStatus.Failed),
                    Reason = reason
                });
            _ = await _store.CompleteGenerationAsync(work.DatasetId, DatasetGenerationWorkStatus.Failed, reason, CancellationToken.None);
        }
        finally
        {
            _events.EvictPlaintext(work.DatasetId);
        }
    }

    /// <summary>
    ///     Commits the terminal outcome, then publishes it. A cancel that lands during the commit throws before anything
    ///     is published, so the hub never announces an outcome the store did not keep.
    /// </summary>
    private async Task CommitThenPublishAsync(Guid datasetId,
        DatasetGenerationWorkStatus workStatus,
        TrainingDatasetStatus state,
        string? reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = await _store.CompleteGenerationAsync(datasetId, workStatus, reason, cancellationToken);
        _ = _events.Append(datasetId, DatasetGenerationEventKind.State, new DatasetGenerationPayload
        {
            State = state.ToString(),
            Reason = reason
        });
    }

    /// <summary>True when a generated user turn restates the teacher instruction instead of posing a request.</summary>
    internal static bool EchoesTeacherPrompt(string userMessage) =>
        TeacherPromptEcho.IsMatch(userMessage.Trim());

    private static string NoUsableSamplesReason(GenerationTally tally)
    {
        var reasons = tally.RejectionReasons
                           .OrderByDescending(pair => pair.Value)
                           .Take(ReportedRejectionReasons)
                           .Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key} ({pair.Value}x)"));
        return string.Create(CultureInfo.InvariantCulture,
            $"No usable samples: all {tally.Rejected} generated samples were rejected. Most frequent reasons: {string.Join("; ", reasons)}");
    }

    private async Task<GenerationTally> GenerateAsync(DatasetGenerationClaimedWork work, CancellationToken cancellationToken)
    {
        // The PINNED body, not the live definition row: an edit between the dataset's creation and this run would otherwise
        // swap the teacher, the tool snapshot or the instructions while the dataset still claims its DefinitionVersion.
        var definition = DatasetDefinitionService.ReadPinnedBody(work.Dataset)
                         ?? throw new TrainingValidationException(DatasetDefinitionService.UnpinnedDatasetReason);
        var plan = BuildPlan(definition);

        // Checked here as well as in the runner: this is the seam that RESOLVES a provider and builds the client, so reaching
        // it with an ext: id would open a live connection to the external endpoint before the runner's guard saw the first turn.
        TrainingModelEligibility.EnsureNotExternal(definition.TeacherModelName, "dataset generation teachers");

        var provider = await _providerResolver.ResolveProviderForModelAsync(definition.TeacherModelName, cancellationToken);
        // One node-local client for the whole run; IChatClient is IDisposable and this one is ours (never the shared singleton).
        using var teacherClient = provider.CreateChatClient(new LocalModelSelection
        {
            ModelName = definition.TeacherModelName,
            ProviderName = provider.ProviderName
        }).WithProviderTelemetry();
        using var criticClient = await CreateCriticClientAsync(definition, cancellationToken);

        var systemInstructions = ComposeSystemInstructions(definition);
        _ = _events.Append(work.DatasetId, DatasetGenerationEventKind.State, new DatasetGenerationPayload
        {
            State = nameof(TrainingDatasetStatus.Generating)
        });

        var tally = new GenerationTally();
        // One set per run: the pipeline refuses a user turn this generation already produced.
        var acceptedUserMessages = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < plan.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = plan[index];
            var request = new StructuredAgentRequest
            {
                ModelName = definition.TeacherModelName,
                SystemInstructions = systemInstructions,
                UserPrompt = ComposeUserPrompt(target, index),
                OutputMode = definition.TeacherOutputMode,
                ResponseSchema = RecordSchema,
                Temperature = definition.Temperature,
                // Per-sample determinism: base seed + sample index, computed as a long and only then formatted back to
                // the string the seed field is carried as.
                Seed = OffsetSeed(definition.BaseSeed, index)
            };

            var completion = await _runner.RunAsync(teacherClient, request, cancellationToken);
            if (!completion.Success)
            {
                await RejectAsync(work.DatasetId, completion.FailureReason, tally, cancellationToken);
                continue;
            }

            var outcome = await _pipeline.ValidateAsync(completion.Text,
                new SampleValidationContext
                {
                    Definition = definition,
                    Kind = target.Kind,
                    RequestedLabel = target.Label,
                    RecordSchema = RecordSchema,
                    CriticChatClient = criticClient,
                    AcceptedUserMessages = acceptedUserMessages
                },
                cancellationToken);
            if (!outcome.Accepted || outcome.Content is null)
            {
                await RejectAsync(work.DatasetId, outcome.RejectionReason, tally, cancellationToken);
                continue;
            }

            var contentJson = JsonSerializer.SerializeToUtf8Bytes(outcome.Content, TrainingJson.Options);
            var append = await _store.AppendSampleAsync(new TrainingSampleInput
                {
                    DatasetId = work.DatasetId,
                    Kind = target.Kind,
                    Label = outcome.Label,
                    ContentJson = contentJson,
                    ValidationJson = JsonSerializer.SerializeToUtf8Bytes(outcome.Validation, TrainingJson.Options),
                    Provenance = TrainingSampleProvenance.Generated,
                    SourceHash = SourceHash(contentJson)
                },
                cancellationToken);
            if (append.Duplicate)
            {
                tally.Count(DuplicateSampleReason);
            }
            else
            {
                tally.Accepted++;
            }

            _ = _events.Append(work.DatasetId,
                append.Duplicate ? DatasetGenerationEventKind.Rejected : DatasetGenerationEventKind.SampleAdded,
                new DatasetGenerationPayload
                {
                    Completed = index + 1,
                    Total = plan.Count,
                    Kind = target.Kind,
                    Label = outcome.Label.ToString(),
                    Reason = append.Duplicate ? "duplicate" : null
                });
        }

        return tally;
    }

    private const string DuplicateSampleReason = "An identical sample already exists in this dataset.";

    private async Task RejectAsync(Guid datasetId, string? reason, GenerationTally tally, CancellationToken cancellationToken)
    {
        tally.Count(reason ?? "(no reason recorded)");
        await _store.RecordRejectedSampleAsync(datasetId, cancellationToken);
        _ = _events.Append(datasetId, DatasetGenerationEventKind.Rejected, new DatasetGenerationPayload
        {
            Reason = reason
        });
        // The hub buffer is transient and evicted when the run terminalizes; the count survives but the reason would not, so
        // log it (invariant: fail-visible, never fail-silent). Reasons are validator/transport messages, never sample content.
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

        var provider = await _providerResolver.ResolveProviderForModelAsync(definition.CriticModelName, cancellationToken);
        return provider.CreateChatClient(new LocalModelSelection
        {
            ModelName = definition.CriticModelName,
            ProviderName = provider.ProviderName
        }).WithProviderTelemetry();
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
        string.Format(CultureInfo.InvariantCulture, TeacherPromptFormat, index + 1, target.Kind, target.Label == TrainingSampleLabel.Good ? "correct" : "incorrect");

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

    /// <summary>What one run produced, so a run that accepted nothing terminalizes as a failure with its reasons.</summary>
    private sealed class GenerationTally
    {
        public int Accepted { get; set; }

        public int Rejected { get; private set; }

        public Dictionary<string, int> RejectionReasons { get; } = new(StringComparer.Ordinal);

        public void Count(string reason)
        {
            Rejected++;
            RejectionReasons[reason] = RejectionReasons.GetValueOrDefault(reason) + 1;
        }
    }
}
