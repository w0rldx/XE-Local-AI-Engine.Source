namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

/// <summary>Bounded execution, polling, and compaction settings for durable inbound MCP agent runs.</summary>
public sealed class McpAgentRunOptions
{
    public const string SectionName = "Mcp:AgentRuns";

    public int MaxConcurrentWorkers { get; init; } = 1;

    public int WatchdogMinutes { get; init; } = 30;

    public int PollIntervalMilliseconds { get; init; } = 250;

    public int CompactionIntervalMinutes { get; init; } = 15;

    public int MaxTaskUtf8Bytes { get; init; } = 32 * 1024;

    public int MaxInstructionsUtf8Bytes { get; init; } = 16 * 1024;

    public int MaxResultCharacters { get; init; } = 24_000;

    public int DefaultListLimit { get; init; } = 20;

    public int MaxListLimit { get; init; } = 50;
}
