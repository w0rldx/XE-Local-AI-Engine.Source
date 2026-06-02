namespace XE_Local_AI_Engine.HostAgent.Linux.Docker;

/// <summary>
///     Outcome of <see cref="IDockerRuntimeClient.ExecInContainerAsync" /> for a sandbox command.
///     <see cref="Completed" /> is <see langword="false" /> when the exec was cancelled or the read-loop was torn
///     down before the process finished; the service maps that to a non-completed <c>SandboxCommandResult</c>.
/// </summary>
public sealed record DockerExecResult
{
    /// <summary>Echo of the request's execution id.</summary>
    public required string ExecutionId { get; init; }

    /// <summary>The command's exit code (<c>-1</c> when it did not complete).</summary>
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
