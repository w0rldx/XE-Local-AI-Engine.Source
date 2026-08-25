namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     A long-lived command running inside a sandbox, with its standard streams left open for the caller to speak a
///     protocol over. Returned by <see cref="ISandboxRuntimeProvider.StartInteractiveAsync" />.
///     <para>
///         <b>Why this exists alongside <see cref="ISandboxRuntimeProvider.ExecuteAsync" />.</b> That one is
///         request/response: it writes an optional string to stdin, closes it, and waits for exit while capping the
///         captured output. That shape fits every workload this engine had until stdio MCP, whose whole protocol is a
///         duplex JSON-RPC conversation over a process that must stay alive between calls. Bolting a streaming mode
///         onto <c>ExecuteAsync</c> would have made every existing caller's contract conditional; a second operation
///         leaves them byte-identical.
///     </para>
///     <para>
///         <b>Disposal kills, it does not wait.</b> Releasing this terminates the command — the transient scope's
///         cgroup first where there is one, then the process group, then the tree — because a protocol peer that has
///         been let go is not going to exit on its own.
///     </para>
/// </summary>
public interface ISandboxInteractiveProcess : IAsyncDisposable
{
    /// <summary>
    ///     The command's standard input. The caller owns what it writes; closing this stream is the protocol's way of
    ///     saying "no more requests", and is not required — disposal is what tears the command down.
    /// </summary>
    Stream StandardInput { get; }

    /// <summary>The command's standard output. Reads return what the command has written.</summary>
    Stream StandardOutput { get; }
}
