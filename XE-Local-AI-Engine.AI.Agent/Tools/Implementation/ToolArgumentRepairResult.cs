namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
///     Builds the structured, model-actionable results a tool returns instead of throwing when a call cannot proceed.
///     Returning a well-shaped result (rather than an exception) turns the framework's function-invocation loop into the
///     repair loop: the model sees exactly what was wrong plus the schema it must satisfy, and self-corrects on the next
///     turn. Messages are deliberately structural — they name the offending property, never echo the supplied argument
///     values — so a malformed call can never leak secrets into chat history, logs, or telemetry.
/// </summary>
internal static class ToolArgumentRepairResult
{
    /// <summary>
    ///     Result for a call whose arguments failed validation (or could not be parsed by the handler). Carries the
    ///     specific <paramref name="reason" /> and the tool's <paramref name="expectedSchema" /> so the model can repair
    ///     and retry.
    /// </summary>
    public static string InvalidArguments(string reason, JsonElement expectedSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new JsonObject
        {
            ["error"] = "invalid_arguments",
            ["reason"] = reason,
            ["expected_schema"] = expectedSchema.ValueKind == JsonValueKind.Undefined
                ? new JsonObject()
                : JsonNode.Parse(expectedSchema.GetRawText()),
            ["hint"] = "Correct the arguments to match expected_schema and call the tool again."
        }.ToJsonString();
    }

    /// <summary>
    ///     Terminal result for a tool that has exhausted its repair budget for the request: the model is told to stop
    ///     calling it so it does not burn the remaining iteration budget looping on the same malformed call.
    /// </summary>
    public static string ToolDisabled(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        return new JsonObject
        {
            ["error"] = "tool_disabled",
            ["reason"] = $"Tool '{toolName}' was disabled for this run after repeated invalid-argument calls.",
            ["hint"] = "Do not call this tool again during this run; continue without it."
        }.ToJsonString();
    }
}
