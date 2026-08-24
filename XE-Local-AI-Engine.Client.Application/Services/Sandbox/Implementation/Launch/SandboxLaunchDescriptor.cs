namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

/// <summary>
///     The resolved wrapper chain for one sandboxed command, plus an honest record of which mechanisms were actually
///     applied. The <c>Applied…</c> flags are what the provider logs and what the marker file records; they are the
///     measured outcome, never the request — a policy asking for a mechanism the host lacks yields a descriptor with
///     that flag false rather than a failure.
/// </summary>
public sealed record SandboxLaunchDescriptor
{
    /// <summary>The executable to start: the outermost wrapper, or the command itself when nothing is wrapped.</summary>
    public required string FileName { get; init; }

    /// <summary>The full argument vector, with the original command and its arguments at the tail.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>
    ///     <see langword="true" /> when the child was launched under <c>setsid</c>, making the started pid a
    ///     process-group leader. Only then may the pid be used as a process-group id for group-kill or orphan reaping.
    /// </summary>
    public bool AppliedProcessGroup { get; init; }

    /// <summary><see langword="true" /> when real memory / PID / CPU ceilings were imposed on the child.</summary>
    public bool AppliedResourceLimits { get; init; }

    /// <summary><see langword="true" /> when the child was placed in a fresh empty network namespace.</summary>
    public bool AppliedNetworkIsolation { get; init; }

    /// <summary>
    ///     Extra environment the WRAPPER needs (the user systemd bus address). These are layered onto the child's
    ///     scrubbed environment and then stripped again by the innermost <c>env -u</c> layer, so they never reach the
    ///     sandboxed executable. Empty unless the resource-limit layer is applied.
    /// </summary>
    public IReadOnlyDictionary<string, string> WrapperEnvironment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    ///     <see langword="true" /> when the command runs inside a mount namespace that does not contain the host
    ///     filesystem. Like every other <c>Applied…</c> flag it is the measured outcome, and it is what the marker file
    ///     and the logs report — never the request.
    /// </summary>
    public bool AppliedFilesystemIsolation { get; init; }

    /// <summary>
    ///     The transient systemd scope the command runs in, or <see langword="null" /> when no named scope was
    ///     created. It is the KILL AUTHORITY: with a PID namespace in the way the engine cannot see the workload's
    ///     processes and the pid it holds belongs to <c>setsid</c>, so signalling the cgroup by unit name is the only
    ///     thing that reaches every process — including one that detached on purpose.
    /// </summary>
    public string? ScopeUnitName { get; init; }

    /// <summary>
    ///     Descriptors and sealed memory files the chain references BY NUMBER, owned by the caller and disposed once
    ///     the process has been started. The child inherits copies at start, so releasing these afterwards is both
    ///     correct and required — one leaked descriptor per command would exhaust the engine's table over a session.
    ///     <see langword="null" /> for every non-isolated launch.
    /// </summary>
    public IDisposable? LaunchResources { get; init; }
}
