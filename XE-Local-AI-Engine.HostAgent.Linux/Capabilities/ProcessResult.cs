namespace XE_Local_AI_Engine.HostAgent.Linux.Capabilities;

public sealed record ProcessResult
{
    public required int ExitCode { get; init; }

    public required string StandardOutput { get; init; }

    public required string StandardError { get; init; }
}
