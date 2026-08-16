namespace XE_Local_AI_Engine.Client.Models;

public sealed record TimeoutSettings
{
    public int InvocationTimeoutSeconds { get; init; } = 600;

    public int ToolCallTimeoutSeconds { get; init; } = 30;

    public int StreamIdleTimeoutSeconds { get; init; } = 60;
}
