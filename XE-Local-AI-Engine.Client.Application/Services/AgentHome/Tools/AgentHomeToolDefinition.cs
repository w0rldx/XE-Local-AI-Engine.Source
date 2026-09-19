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
        "Work on a goal inside the node-scoped, supervised AgentHome sandbox over COPIES of the selected folders. "
        + "A bounded inner agent reads, edits and runs commands on the copy — never on the original — and the run "
        + "returns what it did plus an exported patch. Grant only the actions the goal needs.";

    // `goal.maxLength` (4000) is deliberately NOT clamped, even though llama.cpp's GBNF converter cannot compile a
    // repetition bound that large. Do not "fix" it by lowering the value: the bound here is advisory to the model and
    // must keep matching the server seed and the handler's own (authoritative) validation, so clamping it would silently
    // narrow the contract for every provider to work around one provider's limit. The llama.cpp wire representation is
    // sanitized instead, in LlamaGrammarToolSchemaCompatibility (XE-Local-AI-Engine.Providers.LlamaServer).
    //
    // `selectedFolderIds.items.pattern` MUST keep its alternation INSIDE one pair of anchors
    // (`^(a|b)$`), never as two anchored alternatives (`^a$|^b$`). llama.cpp's json-schema-to-grammar
    // `_visit_pattern` strips exactly one leading `^` and one trailing `$` and then compiles the remainder with a small
    // regex subset in which `^` and `$` are NOT metacharacters (they are absent from its NON_LITERAL_SET), so any
    // INTERIOR anchor is emitted as a literal character into the GBNF. The two-alternative form compiled to
    // `[a-z0-9] root-1{0,63} "$" | "^" root-2{36,36}`, which forced every grammar-constrained value the model produced
    // to carry a trailing `$` — including a correct workspace GUID — and the handler's validator then rejected every
    // call, so the tool could never run. Verified against the pinned build (b10201, commit 8f4646a) by compiling that
    // converter standalone; pinned by LlamaGrammarPatternCompatibilityTests, which walks the whole real offer.
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
