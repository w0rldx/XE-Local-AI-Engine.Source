namespace XE_Local_AI_Engine.Client.Services.AgentHome.Tools;

/// <summary>
///     Worker-side constants for the <c>run_in_agent_home</c> tool.
/// </summary>
/// <remarks>
///     They mirror the server <c>ToolDefinition</c> seed — name, description and parameter schema — and both are
///     authored from the same AgentHome tool contract, so the model-visible schema the worker advertises can never
///     drift from the server's discoverability/approval record.
/// </remarks>
internal static class AgentHomeToolDefinition
{
    public const string ToolName = "run_in_agent_home";

    public const string Description =
        "Work on a goal inside the node-scoped, supervised AgentHome sandbox over COPIES of the selected folders. "
        + "A bounded inner agent reads, edits and runs commands on the copy — never on the original — and the run "
        + "returns what it did plus an exported patch. Grant only the actions the goal needs.";

    /// <summary>The AgentHome tool parameter schema. Kept byte-for-byte aligned with the server seed.</summary>
    /// <remarks>
    ///     <c>goal.maxLength</c> (4000) is never clamped to llama.cpp's smaller GBNF repetition bound: the bound here is advisory to
    ///     the model and must keep matching the server seed and the handler's authoritative validation, so clamping it would narrow
    ///     the contract for every provider to work around one provider's limit. <c>LlamaGrammarToolSchemaCompatibility</c>
    ///     (Providers.LlamaServer) sanitizes the llama.cpp wire representation instead. <c>selectedFolderIds.items.pattern</c> keeps
    ///     its alternation inside ONE pair of anchors for the reason on <see cref="AgentHomeRunToolRequestValidator.SelectedFolderIdPattern" />.
    /// </remarks>
    public const string ParameterSchema = """
                                          {
                                            "type": "object",
                                            "additionalProperties": false,
                                            "required": ["goal", "selectedFolderIds", "allowedActions"],
                                            "properties": {
                                              "goal": { "type": "string", "minLength": 1, "maxLength": 4000 },
                                              "selectedFolderIds": {
                                                "type": "array",
                                                "minItems": 1,
                                                "maxItems": 8,
                                                "items": { "type": "string", "pattern": "^([a-z0-9][a-z0-9-]{0,63}|[0-9a-fA-F-]{36})$" }
                                              },
                                              "runtimeProfile": {
                                                "type": "string",
                                                "enum": ["dotnet-agent-home"],
                                                "default": "dotnet-agent-home"
                                              },
                                              "persona": {
                                                "type": "string",
                                                "enum": ["primary/main"],
                                                "default": "primary/main"
                                              },
                                              "allowedActions": {
                                                "type": "array",
                                                "minItems": 1,
                                                "uniqueItems": true,
                                                "items": {
                                                  "type": "string",
                                                  "enum": ["read_workspace", "write_workspace", "run_commands", "export_patch"]
                                                }
                                              }
                                            }
                                          }
                                          """;
}
