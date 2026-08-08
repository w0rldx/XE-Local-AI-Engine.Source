namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     A single command to execute inside a sandbox. The caller supplies
///     <see cref="ExecutionId" /> so an in-flight command can be targeted by
///     <see cref="ISandboxRuntimeProvider.CancelCommandAsync" />; the result echoes it back. Provider-neutral — no
///     shell string is implied; the provider composes the actual invocation from the executable and arguments.
/// </summary>
public sealed record SandboxCommandRequest
{
    /// <summary>Caller-generated id used to target cancellation and to correlate the result.</summary>
    public required string ExecutionId { get; init; }

    /// <summary>The executable to run inside the sandbox.</summary>
    public required string Executable { get; init; }

    /// <summary>Arguments passed to the executable.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Optional working directory inside the sandbox.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Optional environment variables for the command.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Optional per-command timeout.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Optional standard input piped to the command.</summary>
    public string? StandardInput { get; init; }
}
