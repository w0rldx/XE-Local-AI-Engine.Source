namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

internal sealed class McpAgentRunCancellationHandle
{
    public required Guid RequestId { get; init; }

    public required Guid ClaimToken { get; init; }

    public required long Version { get; init; }
}
