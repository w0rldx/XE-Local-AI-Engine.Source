namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

/// <summary>
///     One started sandbox child, whichever mechanism launched it: a plain <see cref="System.Diagnostics.Process" /> (the Linux chains and
///     the unwrapped Windows child) or an MXC ProcessContainer child (the Windows AppContainer boundary, ADR 0019).
/// </summary>
/// <remarks>
///     Only what <see cref="ProcessSandboxRuntimeProvider" /> and <see cref="SandboxLifecycleRegistry" /> touch. Output capture is wired at
///     start (line callbacks), so the command path never reads a raw stream; the interactive path reads <see cref="StandardOutput" />.
/// </remarks>
internal interface ISandboxChildProcess : IDisposable
{
    /// <summary>The OS process id of the child (for the plain child, the outermost wrapper).</summary>
    int Id { get; }

    /// <summary>Raw stdin, for an interactive peer. Disposing it sends EOF.</summary>
    Stream StandardInput { get; }

    /// <summary>Raw stdout, for an interactive peer; only valid when no stdout line callback was wired at start.</summary>
    Stream StandardOutput { get; }

    /// <summary>The mechanism's OWN timeout fired during the last wait (MXC enforces <c>TimeoutMs</c> inside its wait).</summary>
    bool TimedOut { get; }

    /// <summary>Writes <paramref name="text" /> to stdin and closes it.</summary>
    Task WriteStandardInputAndCloseAsync(string text, CancellationToken cancellationToken);

    /// <summary>Waits for exit and for the captured output to drain; returns the exit code.</summary>
    /// <remarks>On cancellation the MXC child is killed and reaped before this throws; the caller kills either way.</remarks>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Waits for exit without killing anything; <see langword="false" /> when <paramref name="cancellationToken" /> fired first.</summary>
    Task<bool> TryWaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Best-effort kill of the whole child tree. Never throws.</summary>
    void Kill();
}
