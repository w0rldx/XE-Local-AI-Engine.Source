namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>What the pipeline needs beyond the raw completion: the definition it was generated from, and the models to reach.</summary>
public sealed class SampleValidationContext
{
    public required DatasetDefinitionBodyV1 Definition { get; init; }

    public required string Kind { get; init; }

    public required TrainingSampleLabel RequestedLabel { get; init; }

    public required JsonElement RecordSchema { get; init; }

    public required IChatClient? CriticChatClient { get; init; }
}

/// <summary>
///     A pipeline verdict. <see cref="Accepted" /> false means the turn could not be turned into a sample at all and is
///     counted as a rejection; a schema-valid turn that failed a later layer is accepted and retained with the
///     <see cref="TrainingSampleLabel.Bad" /> label (decision #9), never discarded.
/// </summary>
public sealed class SampleValidationOutcome
{
    public required bool Accepted { get; init; }

    public required string? RejectionReason { get; init; }

    public required TrainingSampleLabel Label { get; init; }

    public required TrainingSampleContentV1? Content { get; init; }

    public required TrainingSampleValidationV1 Validation { get; init; }
}

public interface ISampleValidationPipeline
{
    Task<SampleValidationOutcome> ValidateAsync(string rawCompletion, SampleValidationContext context, CancellationToken cancellationToken = default);
}

/// <summary>
///     The ordered validation layers for one generated turn: record schema → tool-name resolution → argument validation
///     → execution → optional critic. EVERY layer's outcome is persisted with the sample (invariant #7), including the
///     ones that passed, so a dataset is auditable without re-running generation.
/// </summary>
public sealed class SampleValidationPipeline : ISampleValidationPipeline
{
    private const string CriticPrompt =
        "You judge one training example. Reply with a JSON object {\"verdict\":\"good\"} or {\"verdict\":\"bad\"} and nothing else.";

    private static readonly JsonElement CriticSchema = JsonDocument.Parse("""{"type":"object","properties":{"verdict":{"type":"string"}},"required":["verdict"]}""").RootElement.Clone();

    private readonly IHeadlessToolExecutor _executor;
    private readonly IStructuredAgentRunner _runner;

    public SampleValidationPipeline(IHeadlessToolExecutor executor, IStructuredAgentRunner runner)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(runner);
        _executor = executor;
        _runner = runner;
    }

    public async Task<SampleValidationOutcome> ValidateAsync(string rawCompletion,
        SampleValidationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var layers = new List<SampleValidationLayerResultV1>();

        // Layer 1 — record schema. Validated against the ORIGINAL schema, never the one the teacher saw: the MEAI
        // adapter rewrites a json-schema response format (all-required, bounds folded into the description), so
        // re-validating against the rewritten copy would silently accept a record the definition never asked for.
        if (!TryReadRecord(rawCompletion, context.RecordSchema, out var record, out var schemaReason))
        {
            layers.Add(new SampleValidationLayerResultV1 { Layer = "record-schema", Passed = false, ScoredBy = "schema", Reason = schemaReason });
            return new SampleValidationOutcome
            {
                Accepted = false,
                RejectionReason = schemaReason,
                Label = context.RequestedLabel,
                Content = null,
                Validation = new TrainingSampleValidationV1
                {
                    Passed = false,
                    Layers = layers
                }
            };
        }

        layers.Add(new SampleValidationLayerResultV1 { Layer = "record-schema", Passed = true, ScoredBy = "schema" });

        var parts = new List<TrainingSamplePartV1>
        {
            new("user", 0, record!.UserMessage)
        };
        var healthy = true;

        if (record.DemonstratesToolCall)
        {
            healthy &= await ValidateToolCallAsync(record, context, parts, layers, cancellationToken);
        }
        else
        {
            layers.Add(new SampleValidationLayerResultV1 { Layer = "tool-name", Passed = true, ScoredBy = "tool-name", Reason = "The sample demonstrates a no-tool answer." });
        }

        if (!string.IsNullOrWhiteSpace(record.AssistantText))
        {
            parts.Add(new TrainingSamplePartV1("text", parts.Count, record.AssistantText));
        }

        // The single-call boundary. Today's record shape can only ever produce one tool part, so this is the pin that
        // turns a later multi-call record into a visible rejection instead of a sample the scorer would grade by its
        // FIRST call and quietly ignore the rest of.
        if (TrainingSampleParts.IsMultiCall(parts))
        {
            layers.Add(new SampleValidationLayerResultV1 { Layer = "tool-name", Passed = false, ScoredBy = "tool-name", Reason = TrainingSampleParts.MultiCallUnsupportedReason });
            return new SampleValidationOutcome
            {
                Accepted = false,
                RejectionReason = TrainingSampleParts.MultiCallUnsupportedReason,
                Label = context.RequestedLabel,
                Content = null,
                Validation = new TrainingSampleValidationV1
                {
                    Passed = false,
                    Layers = layers
                }
            };
        }

        healthy &= await RunCriticAsync(record, context, layers, cancellationToken);

        // Decision #9: a schema-valid turn that failed a later layer is retained as negative training data, not dropped.
        var label = healthy ? context.RequestedLabel : TrainingSampleLabel.Bad;
        return new SampleValidationOutcome
        {
            Accepted = true,
            RejectionReason = null,
            Label = label,
            Content = new TrainingSampleContentV1
            {
                SystemInstructions = context.Definition.SystemInstructions,
                Parts = parts
            },
            Validation = new TrainingSampleValidationV1
            {
                Passed = healthy,
                Layers = layers
            }
        };
    }

    private async Task<bool> ValidateToolCallAsync(TeacherSampleRecordV1 record,
        SampleValidationContext context,
        List<TrainingSamplePartV1> parts,
        List<SampleValidationLayerResultV1> layers,
        CancellationToken cancellationToken)
    {
        // Layer 2 — tool-name resolution against the definition's own snapshot, not the live catalog: the snapshot is
        // what the teacher was shown.
        var toolName = record.EffectiveToolName!;
        var tool = context.Definition.Tools.FirstOrDefault(item => string.Equals(item.Name, toolName, StringComparison.Ordinal));
        if (tool is null)
        {
            layers.Add(new SampleValidationLayerResultV1
            {
                Layer = "tool-name",
                Passed = false,
                ScoredBy = "tool-name",
                Reason = $"The definition's tool snapshot does not contain '{toolName}'."
            });
            return false;
        }

        layers.Add(new SampleValidationLayerResultV1 { Layer = "tool-name", Passed = true, ScoredBy = "tool-name" });

        // Layer 3 — argument validation against the snapshotted parameter schema.
        var argumentsValid = TryValidateArguments(record.ToolArgumentsJson, tool.ParameterSchema, out var argumentsReason);
        layers.Add(new SampleValidationLayerResultV1 { Layer = "arguments", Passed = argumentsValid, ScoredBy = "arguments", Reason = argumentsReason });

        // Layer 4 — execution through the policy-aware headless seam. It runs even when the arguments failed: the
        // outcome (usually a mock miss) is still recorded, so the sample carries the whole picture.
        var outcome = await _executor.ExecuteAsync(toolName, record.ToolArgumentsJson, context.Definition.TeacherModelName, cancellationToken);
        var executed = outcome.Kind is HeadlessToolOutcomeKind.Executed or HeadlessToolOutcomeKind.Mocked;
        layers.Add(new SampleValidationLayerResultV1 { Layer = "execution", Passed = executed, ScoredBy = ExecutionScoredBy(outcome.Kind), Reason = outcome.Reason });

        parts.Add(new TrainingSamplePartV1("tool",
            parts.Count,
            ToolCallId: $"generated-{parts.Count}",
            ToolName: toolName,
            Arguments: record.ToolArgumentsJson,
            Result: outcome.Result,
            IsError: !executed));
        return argumentsValid && executed;
    }

    /// <summary>
    ///     Layer 5 — the optional critic. Deterministic first: a sample whose parts are structurally hollow fails without
    ///     ever reaching a model. The LLM pass runs only when the definition enables it, and fails CLOSED — a critic that
    ///     errors, or answers anything but "good", marks the sample bad.
    /// </summary>
    private async Task<bool> RunCriticAsync(TeacherSampleRecordV1 record,
        SampleValidationContext context,
        List<SampleValidationLayerResultV1> layers,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(record.UserMessage))
        {
            layers.Add(new SampleValidationLayerResultV1 { Layer = "critic", Passed = false, ScoredBy = "critic:deterministic", Reason = "The sample carries no user turn." });
            return false;
        }

        if (string.IsNullOrWhiteSpace(record.AssistantText) && !record.DemonstratesToolCall)
        {
            layers.Add(new SampleValidationLayerResultV1
            {
                Layer = "critic",
                Passed = false,
                ScoredBy = "critic:deterministic",
                Reason = "The sample demonstrates neither an answer nor a tool call."
            });
            return false;
        }

        if (!context.Definition.CriticEnabled || context.CriticChatClient is null || string.IsNullOrWhiteSpace(context.Definition.CriticModelName))
        {
            layers.Add(new SampleValidationLayerResultV1 { Layer = "critic", Passed = true, ScoredBy = "critic:deterministic" });
            return true;
        }

        var result = await _runner.RunAsync(context.CriticChatClient,
                                      new StructuredAgentRequest
                                      {
                                          ModelName = context.Definition.CriticModelName,
                                          SystemInstructions = CriticPrompt,
                                          UserPrompt = JsonSerializer.Serialize(record, TrainingJson.Options),
                                          OutputMode = TeacherOutputMode.ValidateAfter,
                                          ResponseSchema = CriticSchema,
                                          Temperature = 0f,
                                          Seed = null
                                      },
                                      cancellationToken);
        if (!result.Success)
        {
            layers.Add(new SampleValidationLayerResultV1 { Layer = "critic", Passed = false, ScoredBy = "critic:judge", Reason = result.FailureReason });
            return false;
        }

        var verdict = ReadVerdict(result.Text);
        layers.Add(new SampleValidationLayerResultV1 { Layer = "critic", Passed = verdict, ScoredBy = "critic:judge", Reason = verdict ? null : "The critic rejected the sample." });
        return verdict;
    }

    /// <summary>Provenance string for the execution layer, in the <c>DefaultPlaybookEvalJudge</c> ScoredBy style.</summary>
    private static string ExecutionScoredBy(HeadlessToolOutcomeKind kind) =>
        kind switch
        {
            HeadlessToolOutcomeKind.Executed => "execution:executed",
            HeadlessToolOutcomeKind.Mocked => "execution:mocked",
            HeadlessToolOutcomeKind.ValidationOnly => "execution:validation-only",
            _ => "execution:failed"
        };

    private static bool ReadVerdict(string text)
    {
        if (!TryExtractJsonObject(text, out var json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("verdict", out var verdict)
                   && verdict.ValueKind == JsonValueKind.String
                   && string.Equals(verdict.GetString(), "good", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadRecord(string rawCompletion, JsonElement schema, out TeacherSampleRecordV1? record, out string reason)
    {
        record = null;
        if (!TryExtractJsonObject(rawCompletion, out var json))
        {
            reason = "The completion contains no JSON object.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            reason = $"The completion is not valid JSON: {exception.Message}";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                reason = "The completion is not a JSON object.";
                return false;
            }

            // rejectUnknownProperties: false — a teacher that volunteers an extra field has still produced a usable
            // record, and the required/type checks are what decide the sample's validity.
            var arguments = document.RootElement.EnumerateObject()
                                    .ToDictionary(property => property.Name, property => (object?)property.Value, StringComparer.Ordinal);
            var validation = ToolArgumentValidator.CoerceAndValidate(schema, arguments, rejectUnknownProperties: false);
            if (!validation.IsValid)
            {
                reason = validation.Reason ?? "The completion does not satisfy the record schema.";
                return false;
            }

            try
            {
                record = JsonSerializer.Deserialize<TeacherSampleRecordV1>(json, TrainingJson.Options);
            }
            catch (JsonException exception)
            {
                reason = $"The completion could not be read as a sample record: {exception.Message}";
                return false;
            }
        }

        if (record is null)
        {
            reason = "The completion produced an empty sample record.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryValidateArguments(string argumentsJson, string? parameterSchema, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(parameterSchema))
        {
            return true;
        }

        try
        {
            using var schema = JsonDocument.Parse(parameterSchema);
            using var arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (arguments.RootElement.ValueKind != JsonValueKind.Object)
            {
                reason = "The generated tool arguments are not a JSON object.";
                return false;
            }

            var bag = arguments.RootElement.EnumerateObject()
                               .ToDictionary(property => property.Name, property => (object?)property.Value, StringComparer.Ordinal);
            var validation = ToolArgumentValidator.CoerceAndValidate(schema.RootElement, bag);
            reason = validation.Reason;
            return validation.IsValid;
        }
        catch (JsonException exception)
        {
            reason = $"The generated tool arguments could not be validated: {exception.Message}";
            return false;
        }
    }

    /// <summary>
    ///     Extracts the outermost JSON object from a completion. In ValidateAfter mode the teacher may wrap the record in
    ///     prose or a fenced block, so the raw text is not necessarily parseable as-is.
    /// </summary>
    private static bool TryExtractJsonObject(string text, out string json)
    {
        json = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return false;
        }

        json = text[start..(end + 1)];
        return true;
    }
}
