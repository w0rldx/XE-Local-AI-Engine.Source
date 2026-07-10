namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using System.Text;
using System.Text.Json;

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

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("error", "invalid_arguments");
            writer.WriteString("reason", reason);
            writer.WritePropertyName("expected_schema");
            if (expectedSchema.ValueKind == JsonValueKind.Undefined)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
            {
                expectedSchema.WriteTo(writer);
            }

            writer.WriteString("hint", "Correct the arguments to match expected_schema and call the tool again.");
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    ///     Terminal result for a tool that has exhausted its repair budget for the request: the model is told to stop
    ///     calling it so it does not burn the remaining iteration budget looping on the same malformed call.
    /// </summary>
    public static string ToolDisabled(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("error", "tool_disabled");
            writer.WriteString("reason", $"Tool '{toolName}' was disabled for this run after repeated invalid-argument calls.");
            writer.WriteString("hint", "Do not call this tool again during this run; continue without it.");
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
