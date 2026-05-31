namespace XE_Local_AI_Engine.HostAgent.Linux.Docker;

/// <summary>
///     Value object carrying docker container status data.
/// </summary>
public sealed record DockerContainerStatus
{
    public IReadOnlyList<string> NetworkNames { get; init; } = [];

    public required string Name { get; init; }

    public required string ImageReference { get; init; }

    public required string State { get; init; }

    public required bool IsRunning { get; init; }
}
