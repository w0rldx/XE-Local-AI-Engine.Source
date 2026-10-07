namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using System.Text;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;

/// <summary>The <see cref="ISandboxChildProcess" /> over an MXC ProcessContainer child (the Windows AppContainer boundary, ADR 0019).</summary>
/// <remarks>
///     MXC's own <c>Kill()</c> takes the whole contained tree, and its <c>Dispose()</c> kills a still-running child, so no Linux killer
///     (scope unit, process group) ever applies to it.
/// </remarks>
internal sealed class MxcSandboxChildProcess : ISandboxChildProcess
{
    private readonly MxcChildProcess _child;
    private MxcCancellableStdinStream? _standardInput;

    public MxcSandboxChildProcess(MxcChildProcess child)
    {
        _child = child ?? throw new ArgumentNullException(nameof(child));
    }

    public int Id => _child.Id;

    /// <summary>The child's stdin as a <see cref="MxcCancellableStdinStream" />: a cancelled async write kills the child instead of hanging.</summary>
    public Stream StandardInput => _standardInput ??= new MxcCancellableStdinStream(
        _child.StandardInput ?? throw new InvalidOperationException("The MXC child's stdin is not piped."),
        Kill);

    public Stream StandardOutput => _child.StandardOutput ?? throw new InvalidOperationException("The MXC child's stdout is pumped or not piped.");

    public bool TimedOut => _child.TimedOut;

    public async Task WriteStandardInputAndCloseAsync(string text, CancellationToken cancellationToken)
    {
        await using var stream = StandardInput;
        await stream.WriteAsync(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text), cancellationToken);
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => _child.WaitForExitAsync(cancellationToken);

    public Task<bool> TryWaitForExitAsync(CancellationToken cancellationToken) => _child.TryWaitForExitAsync(cancellationToken);

    public void Kill()
    {
        try
        {
            _child.Kill();
        }
        catch (Exception)
        {
            // Best-effort across the native boundary: an exited, disposed or already-reaped child has nothing left to kill.
        }
    }

    public void Dispose() => _child.Dispose();
}
