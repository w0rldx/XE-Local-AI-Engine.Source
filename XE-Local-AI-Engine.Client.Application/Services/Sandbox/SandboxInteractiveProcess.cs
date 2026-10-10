namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     A long-lived command running inside a sandbox, with its standard streams left open for the caller to speak a protocol over.
/// </summary>
/// <remarks>
///     It exists alongside <see cref="ISandboxRuntimeProvider.ExecuteAsync" />, which is request/response and fits every workload this
///     engine had until stdio MCP, whose protocol is a duplex conversation over a process that must stay alive between calls; a streaming
///     mode on <c>ExecuteAsync</c> would have made every existing caller's contract conditional. Disposal KILLS rather than waits — the
///     transient scope's cgroup first where there is one, then the process group, then the tree — because a protocol peer that has been
///     let go is not going to exit on its own.
/// </remarks>
public interface ISandboxInteractiveProcess : IAsyncDisposable
{
    /// <summary>The command's standard input; the caller owns what it writes.</summary>
    /// <remarks>
    ///     Closing this stream is the protocol's way of saying "no more requests" and is not required: disposal is what tears the command
    ///     down.
    /// </remarks>
    Stream StandardInput { get; }

    /// <summary>The command's standard output. Reads return what the command has written.</summary>
    Stream StandardOutput { get; }

    /// <summary>
    ///     Waits briefly for the command to exit and its <c>stderr</c> to drain, then returns the last lines it wrote there
    ///     (bounded, configured environment values redacted), or <see langword="null" /> when it wrote none.
    /// </summary>
    /// <remarks>
    ///     For diagnosing a command that died: a caller that sees the protocol stream end asks for this to say WHY. The
    ///     default has no tail to offer, which is honest for a provider that does not capture one.
    /// </remarks>
    Task<string?> GetStandardErrorTailAsync()
    {
        return Task.FromResult<string?>(null);
    }

    /// <summary>The command's exit code once it has exited, or <see langword="null" /> while it runs or after disposal.</summary>
    int? ExitCode => null;

    /// <summary>The sandbox mechanism's own warnings for this run (MXC's on Windows); empty where the mechanism reports none.</summary>
    IReadOnlyList<string> Warnings => [];
}
