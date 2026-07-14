namespace XE_Local_AI_Engine.Client.Services.AgentHome.Tools;

/// <summary>
///     Worker-side constants for the <c>run_in_agent_home</c> tool. These mirror the server
///     <c>ToolDefinition</c> seed (name / description / parameter schema) authored from the AgentHome tool contract, so
///     the model-visible schema the worker advertises can never drift from the server's discoverability/approval
///     record. Both are authored from the same schema source.
/// </summary>
internal static class AgentHomeToolDefinition
{
    public const string ToolName = "run_in_agent_home";

    public const string Description =
        "Run an agent task inside the node-scoped, supervised AgentHome workspace over selected folders.";

    /// <summary>The AgentHome tool parameter schema. Kept byte-for-byte aligned with the server seed.</summary>
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
                                                "items": { "type": "string", "pattern": "^[a-z0-9][a-z0-9-]{0,63}$|^[0-9a-fA-F-]{36}$" }
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
                                                  "enum": ["read_workspace", "write_workspace", "run_commands", "export_patch", "propose_memory"]
                                                }
                                              }
                                            }
                                          }
                                          """;
}
