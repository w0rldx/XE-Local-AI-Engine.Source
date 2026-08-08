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
}
