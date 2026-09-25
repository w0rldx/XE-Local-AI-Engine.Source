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

    /// <summary>"label|normalised user turn" keys this generation already accepted; the caller owns one set per run.</summary>
    public ISet<string> AcceptedUserMessages { get; init; } = new HashSet<string>(StringComparer.Ordinal);
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
///     The ordered validation layers for one generated turn: record schema → tool-name resolution → argument
///     validation → execution → optional critic.
/// </summary>
/// <remarks>
///     EVERY layer's outcome is persisted with the sample (invariant #7), including the ones that passed, so a
///     dataset is auditable without re-running generation.
/// </remarks>
public sealed class SampleValidationPipeline : ISampleValidationPipeline
{
    private const string EchoedInstructionReason = "The user message repeats the generation instruction instead of posing a request.";

    private const string DuplicateUserMessageReason = "The user message duplicates one already accepted in this generation with the same label.";

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

        // Layer 1 — record schema, validated against the ORIGINAL schema, never the one the teacher saw: the MEAI adapter
        // rewrites a json-schema response format (all-required, bounds in the description), so re-validating there accepts too much.
        if (!TryReadRecord(rawCompletion, context.RecordSchema, out var record, out var schemaReason))
        {
            layers.Add(new SampleValidationLayerResultV1
            {
                Layer = "record-schema",
                Passed = false,
                ScoredBy = "schema",
                Reason = schemaReason
            });
            return Rejected(schemaReason, context, layers);
        }

        layers.Add(new SampleValidationLayerResultV1
        {
            Layer = "record-schema",
            Passed = true,
            ScoredBy = "schema"
        });

        // Not a flaw in the assistant's behaviour but in the generation itself, so these are rejected rather than kept
        // as Bad data: a Bad label on a correct answer would teach the wrong lesson.
        if (DatasetGenerationExecutor.EchoesTeacherPrompt(record!.UserMessage))
        {
            return GenerationDefect(EchoedInstructionReason, context, layers);
        }

        var parts = new List<TrainingSamplePartV1>
        {
            new("user", 0, record.UserMessage)
        };
        var healthy = true;

        if (record.DemonstratesToolCall)
        {
            healthy &= await ValidateToolCallAsync(record, context, parts, layers, cancellationToken);
        }
        else
        {
            layers.Add(new SampleValidationLayerResultV1
            {
                Layer = "tool-name",
                Passed = true,
                ScoredBy = "tool-name",
                Reason = "The sample demonstrates a no-tool answer."
            });
        }

        if (!string.IsNullOrWhiteSpace(record.AssistantText))
        {
            parts.Add(new TrainingSamplePartV1("text", parts.Count, record.AssistantText));
        }

        // The single-call boundary. Today's record shape can only ever produce one tool part, so this pin turns a later
        // multi-call record into a visible rejection instead of a sample the scorer grades by its FIRST call, ignoring the rest.
        if (TrainingSampleParts.IsMultiCall(parts))
        {
            layers.Add(new SampleValidationLayerResultV1
            {
                Layer = "tool-name",
                Passed = false,
                ScoredBy = "tool-name",
                Reason = TrainingSampleParts.MultiCallUnsupportedReason
            });
            return Rejected(TrainingSampleParts.MultiCallUnsupportedReason, context, layers);
        }

        healthy &= await RunCriticAsync(record, context, layers, cancellationToken);

        // Decision #9: a schema-valid turn that failed a later layer is retained as negative training data, not dropped.
        var label = healthy ? context.RequestedLabel : TrainingSampleLabel.Bad;

        // Keyed by the FINAL label: a Good and a Bad sample sharing one prompt are a contrastive pair, not a duplicate.
        var userMessageKey = NormalizeUserMessage(record.UserMessage);
        if (userMessageKey.Length > 0 && !context.AcceptedUserMessages.Add(label.ToString() + "|" + userMessageKey))
        {
            return GenerationDefect(DuplicateUserMessageReason, context, layers);
        }
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

    private static SampleValidationOutcome Rejected(string reason, SampleValidationContext context, List<SampleValidationLayerResultV1> layers) =>
        new()
        {
            Accepted = false,
            RejectionReason = reason,
            Label = context.RequestedLabel,
            Content = null,
            Validation = new TrainingSampleValidationV1
            {
                Passed = false,
                Layers = layers
            }
        };

    private static SampleValidationOutcome GenerationDefect(string reason, SampleValidationContext context, List<SampleValidationLayerResultV1> layers)
    {
        layers.Add(new SampleValidationLayerResultV1
        {
            Layer = "critic",
            Passed = false,
            ScoredBy = "critic:deterministic",
            Reason = reason
        });
        return Rejected(reason, context, layers);
    }

    /// <summary>Duplicate key for a user turn: trimmed, case-folded, whitespace runs collapsed to one space.</summary>
    internal static string NormalizeUserMessage(string? userMessage) =>
        string.Join(' ', (userMessage ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

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

        layers.Add(new SampleValidationLayerResultV1
        {
            Layer = "tool-name",
            Passed = true,
            ScoredBy = "tool-name"
        });

        // Layer 3 — argument validation against the snapshotted parameter schema.
        var argumentsValid = TryValidateArguments(record.ToolArgumentsJson, tool.ParameterSchema, out var argumentsReason);
        layers.Add(new SampleValidationLayerResultV1
        {
            Layer = "arguments",
            Passed = argumentsValid,
            ScoredBy = "arguments",
            Reason = argumentsReason
        });

        // Layer 4 — execution through the policy-aware headless seam. It runs even when the arguments failed: the
        // outcome (usually a mock miss) is still recorded, so the sample carries the whole picture.
        var outcome = await _executor.ExecuteAsync(toolName, record.ToolArgumentsJson, context.Definition.TeacherModelName, cancellationToken);
        var executed = outcome.Kind is HeadlessToolOutcomeKind.Executed or HeadlessToolOutcomeKind.Mocked;
        layers.Add(new SampleValidationLayerResultV1
        {
            Layer = "execution",
            Passed = executed,
            ScoredBy = ExecutionScoredBy(outcome.Kind),
            Reason = outcome.Reason
        });

        parts.Add(new TrainingSamplePartV1("tool",
            parts.Count,
            ToolCallId: $"generated-{parts.Count}",
            ToolName: toolName,
            Arguments: record.ToolArgumentsJson,
            Result: outcome.Result,
            IsError: !executed));
        return argumentsValid && executed;
    }

    /// <summary>Layer 5 — the optional critic.</summary>
    /// <remarks>
    ///     Deterministic first: a sample whose parts are structurally hollow fails without ever reaching a model. The
    ///     LLM pass runs only when the definition enables it, and fails CLOSED — a critic that errors, or answers
    ///     anything but "good", marks the sample bad.
    /// </remarks>
    private async Task<bool> RunCriticAsync(TeacherSampleRecordV1 record,
        SampleValidationContext context,
        List<SampleValidationLayerResultV1> layers,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(record.UserMessage))
        {
            layers.Add(new SampleValidationLayerResultV1
            {
                Layer = "critic",
                Passed = false,
                ScoredBy = "critic:deterministic",
                Reason = "The sample carries no user turn."
            });
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
            layers.Add(new SampleValidationLayerResultV1
            {
                Layer = "critic",
                Passed = true,
                ScoredBy = "critic:deterministic"
            });
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
            layers.Add(new SampleValidationLayerResultV1
            {
                Layer = "critic",
                Passed = false,
                ScoredBy = "critic:judge",
                Reason = result.FailureReason
            });
            return false;
        }

        var verdict = ReadVerdict(result.Text);
        layers.Add(new SampleValidationLayerResultV1
        {
            Layer = "critic",
            Passed = verdict,
            ScoredBy = "critic:judge",
            Reason = verdict ? null : "The critic rejected the sample."
        });
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
        if (!TryExtractJsonObject(text, out var json, out _))
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
        if (!TryExtractJsonObject(rawCompletion, out var json, out reason))
        {
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
                // Property NAMES only (never values): enough to tell a teacher that ignored the schema from one that broke it.
                var found = string.Join(", ", arguments.Keys.Take(8).Select(name => name.Length > 40 ? name[..40] : name));
                reason = (validation.Reason ?? "The completion does not satisfy the record schema.")
                         + (found.Length > 0 ? $" The record carried: {found}." : " The record was an empty object.");
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
    ///     Extracts the record from a completion. In ValidateAfter mode the teacher may wrap it in prose, a fenced block
    ///     or an inline think block, any of which can carry braces of their own.
    /// </summary>
    /// <remarks>
    ///     Only a LEADING think block is dropped, through its first closing tag, so a record whose string holds a
    ///     literal closing tag stays intact. Of the balanced top-level objects left, the LAST that parses wins (a model
    ///     drafts before it answers); with none, the first-to-last-brace slice keeps the parse error informative. An
    ///     unclosed object that opens with a quoted key is a truncated record, never a source of nested candidates.
    /// </remarks>
    private static bool TryExtractJsonObject(string text, out string json, out string failureReason)
    {
        json = string.Empty;
        failureReason = "The completion contains no JSON object.";
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        const string thinkClose = "</think>";
        var thinkEnd = text.IndexOf(thinkClose, StringComparison.OrdinalIgnoreCase);
        if (thinkEnd >= 0 && text.TrimStart().StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
        {
            text = text[(thinkEnd + thinkClose.Length)..];
        }

        var candidates = BalancedObjects(text, out var truncated);
        if (truncated)
        {
            // Codex-found: rescanning a cut-off record promoted its nested draft object to an accepted sample.
            failureReason = "The completion's JSON record is incomplete.";
            return false;
        }

        for (var index = candidates.Count - 1; index >= 0; index--)
        {
            if (ParsesAsObject(candidates[index]))
            {
                json = candidates[index];
                return true;
            }
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

    /// <summary>Top-level <c>{...}</c> spans; quotes only open a string inside an object, so prose apostrophes and quotes are inert.</summary>
    private static List<string> BalancedObjects(string text, out bool truncated)
    {
        var objects = new List<string>();
        var offset = 0;
        truncated = false;
        while (offset < text.Length)
        {
            var unclosedAt = ScanBalancedObjects(text, offset, objects);
            if (unclosedAt < 0)
            {
                break;
            }

            if (OpensWithQuotedKey(text, unclosedAt))
            {
                truncated = true;
                break;
            }

            // Rescan after the stray brace. Quadratic on many of them; the output budget bounds the text.
            offset = unclosedAt + 1;
        }

        return objects;
    }

    /// <summary>A <c>{</c> whose next non-whitespace character is a quote starts a JSON object, not prose.</summary>
    private static bool OpensWithQuotedKey(string text, int braceIndex)
    {
        var next = text.AsSpan(braceIndex + 1).TrimStart();
        return next.Length > 0 && next[0] == '"';
    }

    /// <summary>Adds the closed top-level objects from <paramref name="offset" /> on; returns where an unclosed one began, or -1.</summary>
    private static int ScanBalancedObjects(string text, int offset, List<string> objects)
    {
        var depth = 0;
        var start = 0;
        var inString = false;
        var escaped = false;
        for (var index = offset; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (character)
            {
                case '"' when depth > 0:
                    inString = true;
                    break;
                case '{':
                    if (depth++ == 0)
                    {
                        start = index;
                    }

                    break;
                case '}' when depth > 0:
                    if (--depth == 0)
                    {
                        objects.Add(text[start..(index + 1)]);
                    }

                    break;
            }
        }

        return depth > 0 ? start : -1;
    }

    private static bool ParsesAsObject(string candidate)
    {
        try
        {
            using var document = JsonDocument.Parse(candidate);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
