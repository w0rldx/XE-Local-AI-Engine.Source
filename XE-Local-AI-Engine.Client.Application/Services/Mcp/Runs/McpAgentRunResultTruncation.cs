namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

/// <summary>Clips an MCP run result to the configured bound with a visible marker, shared by run_agent and the durable run path.</summary>
/// <remarks>
///     An MCP client caps tool output (Claude Code at about 25k tokens by default), so an unbounded local-model answer would be cut
///     by the client with no hint why. The clipped text, marker included, never exceeds the bound, and the marker is how
///     <c>get_agent_run</c> recognises a clipped result without an extra stored column.
/// </remarks>
internal static class McpAgentRunResultTruncation
{
    public const string Marker = "\n\n[output truncated by the XE Local AI Engine MCP server]";

    public static string Apply(string content, int maxLength)
    {
        if (content.Length <= maxLength)
        {
            return content;
        }

        return maxLength <= Marker.Length
            ? content[..maxLength]
            : string.Concat(content.AsSpan(0, maxLength - Marker.Length), Marker);
    }

    public static bool IsTruncated(string result, int maxLength) =>
        result.Length > maxLength || result.EndsWith(Marker, StringComparison.Ordinal);
}
