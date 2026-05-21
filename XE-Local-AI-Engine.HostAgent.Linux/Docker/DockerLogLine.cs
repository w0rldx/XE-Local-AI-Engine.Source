namespace XE_Local_AI_Engine.HostAgent.Linux.Docker;

public sealed record DockerLogLine
{
    public required string ContainerName { get; init; }

    public required string Stream { get; init; }

    public required string Line { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }
}
