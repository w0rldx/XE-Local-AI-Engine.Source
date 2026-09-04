namespace XE_Local_AI_Engine.Client.Services.Integrations.Tools;

/// <summary>
///     Name, description and model-visible schema for the one built-in tool an integration execution is additionally
///     offered. The handler advertises its schema from here and the offer accessor builds its descriptor from the same
///     constants, so what the model is offered cannot drift from what the handler validates.
///     <para>
///         <b>The category is a documented stretch.</b> <c>ToolCategory.ReadLocal</c> means "read-only, node-local,
///         side-effect-free", and this tool does write two rows — its own event and the execution's output counters —
///         and hands bytes to the caller that started the run. It touches no file, process or network, and reaches
///         nothing the caller did not already reach, but it is not literally side-effect-free. A fifth
///         <c>IntegrationEgress</c> category is declined for V1: it would change the enum, every policy path that
///         switches on it and the node-policy configuration surface, for one tool whose approval flag the coordinator
///         already recomposes through that same policy.
///     </para>
///     <para>
///         <c>payload</c> is deliberately UNTYPED. The argument validator skips the type check for a property whose
///         subschema declares none, so any JSON value passes; if a provider's grammar compiler ever refuses the empty
///         subschema, the documented fallback is <c>{"type": "object"}</c>.
///     </para>
/// </summary>
internal static class EmitOutputToolDefinition
{
    public const string ToolName = "emit_output";

    /// <summary>What a call that names no media type is recorded and streamed as.</summary>
    public const string DefaultContentType = "application/json";

    /// <summary>
    ///     A pre-parse guard on the raw argument string, not the real bound: the authoritative check is on the COMPOSED
    ///     envelope's plaintext UTF-8 length, which the handler measures after parsing.
    /// </summary>
    public const int MaxJsonArgumentsLength = 512 * 1024;

    public const string Description =
        "Deliver a structured result to the external system that started this run. The payload is passed through "
        + "verbatim and is not shown to the user. Call it once per result; do not repeat the payload in your reply.";

    public const string ParameterSchema = """
                                          {
                                            "type": "object",
                                            "additionalProperties": false,
                                            "required": ["payload"],
                                            "properties": {
                                              "contentType": { "type": "string", "maxLength": 128 },
                                              "payload": { }
                                            }
                                          }
                                          """;
}
