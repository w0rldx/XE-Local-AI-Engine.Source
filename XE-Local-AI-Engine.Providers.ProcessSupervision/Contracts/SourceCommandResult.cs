namespace XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>Exit code and, when the caller asked to capture it, the bounded output of one build command.</summary>
public sealed class SourceCommandResult
{
    public required int ExitCode { get; init; }

    public required string StandardOutput { get; init; }

    public required string StandardError { get; init; }
}
