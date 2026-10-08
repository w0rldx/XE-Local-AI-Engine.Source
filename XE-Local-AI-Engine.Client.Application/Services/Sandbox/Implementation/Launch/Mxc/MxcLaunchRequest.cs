namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;

/// <summary>
///     One command to run under the MXC ProcessContainer, in engine terms. <see cref="MxcPolicyMapper.Build" /> turns it into the
///     SDK's <c>ContainerRequest</c>.
/// </summary>
public sealed class MxcLaunchRequest
{
    /// <summary>The program to start, as a host path or a name resolved through <c>PATH</c>.</summary>
    public required string Executable { get; init; }

    /// <summary>The argv tail, quoted by <see cref="WindowsCommandLine.Join" />.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>The child's working directory; normally inside <see cref="JailRoot" />.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    ///     The child's complete environment, used verbatim. MXC merges nothing into a supplied block, so the caller passes the Windows
    ///     basics (<c>SystemRoot</c>, <c>ComSpec</c>, <c>PATH</c>, <c>PATHEXT</c>, <c>TEMP</c>, a jail <c>LOCALAPPDATA</c>, ...) or the launch fails.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    /// <summary>The one writable tree.</summary>
    public required string JailRoot { get; init; }

    /// <summary>Engine-granted trees the child may read but not write.</summary>
    public required IReadOnlyList<string> ReadOnlyTrees { get; init; }

    /// <summary>Trees the child must never reach: the user profile, the node data root, the engine directory.</summary>
    public required IReadOnlyList<string> DeniedRoots { get; init; }

    /// <summary>The command timeout; <see langword="null" /> leaves MXC unbounded (the engine's own cancellation still applies).</summary>
    public TimeSpan? Timeout { get; init; }
}
