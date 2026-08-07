namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

internal static class McpAgentRunText
{
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
