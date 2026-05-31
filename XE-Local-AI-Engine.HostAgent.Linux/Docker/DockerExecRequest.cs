namespace XE_Local_AI_Engine.HostAgent.Linux.Docker;

/// <summary>
///     A single command to exec inside a sandbox container (local-container sandbox plan §4.1). Provider-neutral — the
///     executable and arguments are passed as an argv list, never a shell string. <see cref="ExecutionId" /> lets an
///     in-flight exec be targeted by <see cref="IDockerRuntimeClient.ExecInContainerAsync" />'s cancellation
///     bookkeeping (D9): cancelling the read-loop is best-effort and does not hard-kill the process.
/// </summary>
public sealed record DockerExecRequest
{
    /// <summary>Caller-supplied id used to correlate the result and to target best-effort cancellation.</summary>
    public required string ExecutionId { get; init; }

    /// <summary>The executable to run inside the container.</summary>
    public required string Executable { get; init; }

    /// <summary>Arguments passed to the executable (argv tail).</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Optional working directory inside the container.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Optional environment variables for the command.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Optional standard input piped to the command.</summary>
    public ReadOnlyMemory<byte> StandardInput { get; init; } = ReadOnlyMemory<byte>.Empty;

    /// <summary>Optional per-command timeout; <see langword="null" /> or non-positive means no timeout.</summary>
    public TimeSpan? Timeout { get; init; }
}
