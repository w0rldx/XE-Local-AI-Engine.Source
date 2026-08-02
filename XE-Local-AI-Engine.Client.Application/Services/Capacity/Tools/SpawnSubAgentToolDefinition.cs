namespace XE_Local_AI_Engine.Client.Services.Capacity.Tools;

/// <summary>
///     Worker-side name / description / parameter-schema constants for the <c>spawn_subagent</c> tool. The handler
///     advertises its model-visible schema from here and the offer provider merges the same descriptor into the offer,
///     so the schema the model is offered can never drift from what the handler validates. The schema is advisory to the
///     model; the handler's own validation (and the capacity gate) are authoritative.
/// </summary>
internal static class SpawnSubAgentToolDefinition
{
    public const string ToolName = "spawn_subagent";

    public const string Description =
        "Spawn a sub-agent bound to a model to handle a delegated task and return its result. Provide exactly one of "
        + "subAgentKey (a saved agent's id or name) or modelId (a model to bind directly). Spawns are capacity-gated: a "
        + "spawn that would exceed the node's memory or concurrency limits is declined with a reason. Sub-agents cannot "
        + "themselves spawn.";

    // `task.maxLength` and `instructions.maxLength` (8000 each) are deliberately NOT clamped, even though llama.cpp's
    // GBNF converter cannot compile repetition bounds that large. Do not "fix" them by lowering the values: the bounds
    // are advisory to the model and the handler's own validation is authoritative, so clamping would narrow the contract
    // for every provider to work around one provider's limit. The llama.cpp wire representation is sanitized instead, in
    // LlamaGrammarToolSchemaCompatibility (XE-Local-AI-Engine.Providers.LlamaServer).
    /// <summary>The spawn tool parameter schema. Exactly one of subAgentKey / modelId, plus a required task.</summary>
    public const string ParameterSchema = """
                                          {
                                            "type": "object",
                                            "additionalProperties": false,
                                            "required": ["task"],
                                            "properties": {
                                              "subAgentKey": { "type": "string", "minLength": 1, "maxLength": 256 },
                                              "modelId": { "type": "string", "minLength": 1, "maxLength": 256 },
                                              "task": { "type": "string", "minLength": 1, "maxLength": 8000 },
                                              "instructions": { "type": "string", "maxLength": 8000 }
                                            }
                                          }
                                          """;
}
