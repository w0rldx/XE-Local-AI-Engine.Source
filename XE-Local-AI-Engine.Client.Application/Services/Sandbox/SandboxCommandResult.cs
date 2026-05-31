namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Outcome of a <see cref="ISandboxRuntimeProvider.ExecuteAsync" /> call.
///     <see cref="Completed" /> is <see langword="false" /> when the command was cancelled or the sandbox was killed
///     before it finished. The real provider must redact host paths from <see cref="StandardError" /> at Information
///     level — that is a logging rule for the provider, not part of this DTO.
/// </summary>
public sealed record SandboxCommandResult
{
    /// <summary>Echo of the request's execution id.</summary>
    public required string ExecutionId { get; init; }

    /// <summary>The command's exit code.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Captured standard output.</summary>
    public string StandardOutput { get; init; } = string.Empty;

    /// <summary>Captured standard error.</summary>
    public string StandardError { get; init; } = string.Empty;

    /// <summary>Whether the command ran to completion (false when cancelled or killed mid-flight).</summary>
    public required bool Completed { get; init; }

    /// <summary>How long the command ran.</summary>
    public TimeSpan Duration { get; init; }
}
