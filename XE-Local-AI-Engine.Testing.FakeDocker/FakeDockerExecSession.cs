namespace XE_Local_AI_Engine.Testing.FakeDocker;

/// <summary>
///     One <c>exec</c> the client created. It lives from <c>POST /containers/{id}/exec</c> until the test ends: the
///     exit code the inspect route reports is recorded when the start route runs, exactly as the daemon fills it in
///     only once the process has exited.
/// </summary>
public sealed class FakeDockerExecSession
{
    /// <summary>The exec id the create route minted.</summary>
    public required string Id { get; init; }

    /// <summary>The container the exec belongs to.</summary>
    public required string ContainerId { get; init; }

    /// <summary>The executable and its arguments joined by single spaces — the exec scripting key.</summary>
    public required string CommandLine { get; init; }

    /// <summary>The working directory the client asked for, recorded so a test can assert on it.</summary>
    public string WorkingDirectory { get; init; } = "";

    /// <summary>The exit code, or null while the exec has not been started.</summary>
    public long? ExitCode { get; set; }
}
