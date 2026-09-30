namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

internal static class McpAgentRunText
{
    /// <summary>The code for a run whose retained result payload has expired.</summary>
    public const string ResultExpiredCode = "result_expired";

    /// <summary>The one caller-facing text for an over-long task, shared by run_agent and start_agent_run so both name the same bound.</summary>
    public static string TaskTooLargeMessage(int maxTaskUtf8Bytes) =>
        $"Cannot run: the task exceeds the {maxTaskUtf8Bytes / 1024} KiB UTF-8 bound.";

    public static string ToLowercaseInvariant<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        var text = value.ToString();
        return string.Create(text.Length, text, static (destination, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                destination[index] = char.ToLowerInvariant(source[index]);
            }
        });
    }
}
