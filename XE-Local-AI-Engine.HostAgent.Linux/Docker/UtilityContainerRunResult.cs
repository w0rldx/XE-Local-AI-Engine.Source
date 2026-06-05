namespace XE_Local_AI_Engine.HostAgent.Linux.Docker;

/// <summary>
///     Outcome of <see cref="IDockerRuntimeClient.RunUtilityContainerAsync" />. <see cref="Completed" />
///     is <see langword="false" /> when the run was cancelled or timed out before the container exited (the service maps
///     that onto a CANCELLED/TIMED_OUT terminal status); on a non-completed run <see cref="ExitCode" /> is <c>-1</c>.
/// </summary>
public sealed record UtilityContainerRunResult
{
    /// <summary>The container's exit code (<c>-1</c> when it did not complete).</summary>
    public required int ExitCode { get; init; }

    /// <summary>Captured standard output (the raw JSON for a successful llmfit run).</summary>
    public string StandardOutput { get; init; } = string.Empty;

    /// <summary>Captured standard error.</summary>
    public string StandardError { get; init; } = string.Empty;

    /// <summary>Whether the container ran to completion (false when cancelled or timed out mid-flight).</summary>
    public required bool Completed { get; init; }

    /// <summary>How long the run took.</summary>
    public TimeSpan Duration { get; init; }
}
