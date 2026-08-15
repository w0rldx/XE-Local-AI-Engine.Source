namespace XE_Local_AI_Engine.Client.Services.Training.Evaluation;

using System.Text.Json;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

/// <summary>
///     The deterministic scorer: one hold-out sample's expectation against what the model actually called. No model is
///     consulted — every verdict here is reproducible from the persisted sample and the persisted response, which is
///     what lets a comparison report be recomputed from storage rather than trusted.
/// </summary>
/// <remarks>
///     <para>
///         The <c>ScoredBy</c> provenance follows <c>DefaultPlaybookEvalJudge</c>: v1 writes only
///         <see cref="Deterministic" />. <c>judge</c> is reserved for a later LLM scorer so an existing results blob
///         can say which verdicts a model produced and which a rule did.
///     </para>
///     <para>
///         Whether a sample is a no-tool sample is decided STRUCTURALLY — the frozen trajectory carries no tool part —
///         not from its kind label. The kind vocabulary belongs to the dataset definition and an operator is free to
///         name a no-tool kind anything; the trajectory cannot lie about what it demonstrates.
///     </para>
/// </remarks>
internal static class EvaluationScorer
{
    /// <summary>The only provenance v1 emits.</summary>
    public const string Deterministic = "deterministic";

    public static TrainingEvaluationResultEntry Score(Guid sampleId,
        string kind,
        EvaluationExpectation expectation,
        IReadOnlyList<EvaluationToolCall> actual)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentNullException.ThrowIfNull(actual);

        if (string.IsNullOrWhiteSpace(expectation.ToolName))
        {
            return actual.Count == 0
                ? Pass(sampleId, kind)
                : Fail(sampleId, kind, $"The sample demonstrates a no-tool answer but the model called '{actual[0].ToolName}'.");
        }

        if (actual.Count == 0)
        {
            return Fail(sampleId, kind, $"The model made no tool call; '{expectation.ToolName}' was expected.");
        }

        if (actual.Count > 1)
        {
            return Fail(sampleId, kind, $"The model made {actual.Count} tool calls; exactly one was expected.");
        }

        var call = actual[0];
        if (!string.Equals(call.ToolName, expectation.ToolName, StringComparison.Ordinal))
        {
            return Fail(sampleId, kind, $"The model called '{call.ToolName}'; '{expectation.ToolName}' was expected.");
        }

        if (!ArgumentsSatisfySchema(call.ArgumentsJson, expectation.ParameterSchema, out var schemaReason))
        {
            return Fail(sampleId, kind, schemaReason);
        }

        return ArgumentsMatch(expectation.ArgumentsJson, call.ArgumentsJson)
            ? Pass(sampleId, kind)
            : Fail(sampleId, kind, "The tool arguments do not match the expected call.");
    }

    /// <summary>
    ///     Reads what a frozen sample expects. The trajectory mirrors the chat <c>parts[]</c> shape, so the expectation
    ///     is the first tool part; a sample without one expects no call at all.
    /// </summary>
    public static EvaluationExpectation ReadExpectation(TrainingSampleContentV1 content, IReadOnlyList<DatasetToolSnapshotV1> tools)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(tools);
        var toolPart = content.Parts.FirstOrDefault(part => string.Equals(part.Kind, "tool", StringComparison.Ordinal)
                                                            && !string.IsNullOrWhiteSpace(part.ToolName));
        if (toolPart?.ToolName is not { Length: > 0 } toolName)
        {
            return new EvaluationExpectation(ToolName: null, ArgumentsJson: null, ParameterSchema: null);
        }

        var snapshot = tools.FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal));
        return new EvaluationExpectation(toolName, toolPart.Arguments, snapshot?.ParameterSchema);
    }

    /// <summary>The user turn the model is asked to answer — the first user part of the frozen trajectory.</summary>
    public static string? ReadUserPrompt(TrainingSampleContentV1 content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content.Parts.FirstOrDefault(part => string.Equals(part.Kind, "user", StringComparison.Ordinal))?.Content;
    }

    private static bool ArgumentsSatisfySchema(string argumentsJson, string? parameterSchema, out string reason)
    {
        reason = string.Empty;
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
                reason = "The model's tool arguments are not a JSON object.";
                return false;
            }

            // The same validator the generation pipeline's argument layer uses, so "valid arguments" means one thing
            // across the module rather than two subtly different things.
            var bag = arguments.RootElement.EnumerateObject()
                               .ToDictionary(property => property.Name, property => (object?)property.Value, StringComparer.Ordinal);
            var validation = ToolArgumentValidator.CoerceAndValidate(schema.RootElement, bag);
            if (validation.IsValid)
            {
                return true;
            }

            reason = validation.Reason ?? "The model's tool arguments do not satisfy the tool's parameter schema.";
            return false;
        }
        catch (JsonException exception)
        {
            reason = $"The model's tool arguments could not be validated: {exception.Message}";
            return false;
        }
    }

    /// <summary>
    ///     Value equality over two argument objects, order-insensitive. Property ORDER and whitespace are formatting,
    ///     not meaning, so a model that emits the same call with its keys in another order has to pass.
    /// </summary>
    internal static bool ArgumentsMatch(string? expectedJson, string? actualJson)
    {
        try
        {
            using var expected = JsonDocument.Parse(string.IsNullOrWhiteSpace(expectedJson) ? "{}" : expectedJson);
            using var actual = JsonDocument.Parse(string.IsNullOrWhiteSpace(actualJson) ? "{}" : actualJson);
            return Equivalent(expected.RootElement, actual.RootElement);
        }
        catch (JsonException)
        {
            // An unparseable expectation cannot be matched against anything; that is a failed sample, not a crash.
            return false;
        }
    }

    private static bool Equivalent(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                var leftProperties = left.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
                var rightProperties = right.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
                return leftProperties.Count == rightProperties.Count
                       && leftProperties.All(entry => rightProperties.TryGetValue(entry.Key, out var other) && Equivalent(entry.Value, other));
            case JsonValueKind.Array:
                // Array ORDER is meaning, unlike property order: a call with reversed arguments is a different call.
                var leftItems = left.EnumerateArray().ToArray();
                var rightItems = right.EnumerateArray().ToArray();
                return leftItems.Length == rightItems.Length
                       && leftItems.Zip(rightItems).All(pair => Equivalent(pair.First, pair.Second));
            case JsonValueKind.Number:
                // Compared as decimals rather than as text: 1 and 1.0 are the same argument value, and decimal
                // equality is exact where a binary float's would not be.
                return left.TryGetDecimal(out var leftNumber)
                       && right.TryGetDecimal(out var rightNumber)
                       && leftNumber == rightNumber;
            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Undefined:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
            default:
                return true;
        }
    }

    private static TrainingEvaluationResultEntry Pass(Guid sampleId, string kind) =>
        new(sampleId, kind, Passed: true, Deterministic);

    private static TrainingEvaluationResultEntry Fail(Guid sampleId, string kind, string reason) =>
        new(sampleId, kind, Passed: false, Deterministic, reason);
}
