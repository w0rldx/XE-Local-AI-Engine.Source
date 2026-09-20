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
}
