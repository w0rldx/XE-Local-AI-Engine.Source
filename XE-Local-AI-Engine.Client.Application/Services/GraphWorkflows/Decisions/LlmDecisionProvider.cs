namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Decisions;

using System.Text.Json;
using System.Text.Json.Nodes;
using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     The <c>llm</c> decision provider: a grammar-enforced enum over the node's labels, run as an ordinary LLM call.
/// </summary>
/// <remarks>
///     The response schema <c>{ choice: { enum: labels } }</c> is what makes the answer one of the labels on
///     llama-server; the labels are also listed in the prompt, because a model constrained to an enum it was never
///     shown picks blindly. Reasoning is off and temperature is 0: the answer is the label, reproducibly.
/// </remarks>
internal sealed class LlmDecisionProvider : IGraphWorkflowDecisionProvider
{
    public const string ProviderName = "llm";

    private const string SystemPrompt =
        "You are a classifier inside an automated workflow. Read the question and any input data, then choose exactly one "
        + "of the listed labels. Reply only with the JSON object {\"choice\": \"<label>\"}.";

    public string Name => ProviderName;

    public GraphWorkflowLlmCallConfig Lower(GraphWorkflowDecisionModelConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var labels = new JsonArray([.. config.Labels.Select(static label => JsonValue.Create(label))]);
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["choice"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = labels
                }
            },
            ["required"] = new JsonArray("choice")
        };

        return new GraphWorkflowLlmCallConfig
        {
            Model = config.Model,
            SystemPrompt = SystemPrompt,
            Prompt = $"{config.Question}\n\nLabels: {string.Join(", ", config.Labels)}",
            InputBindings = config.InputBindings,
            ReasoningEffort = "none",
            ResponseJsonSchema = JsonSerializer.SerializeToElement(schema),

            // Temperature 0: the same input routes the same way on a re-run, which is what an operator debugging a branch needs.
            SamplingOptions = new SamplingOptions
            {
                Temperature = 0
            }
        };
    }

    public DecisionResult Interpret(string text, JsonElement? json) =>
        new()
        {
            Choice = json is { ValueKind: JsonValueKind.Object } answer && answer.TryGetProperty("choice", out var choice) && choice.ValueKind == JsonValueKind.String
                ? choice.GetString()
                : null,
            Confidence = null,
            Probabilities = null
        };
}
