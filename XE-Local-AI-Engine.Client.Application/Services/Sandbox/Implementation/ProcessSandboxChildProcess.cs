namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using System.Diagnostics;

/// <summary>The <see cref="ISandboxChildProcess" /> over a started <see cref="Process" />: the behaviour the provider always had.</summary>
internal sealed class ProcessSandboxChildProcess : ISandboxChildProcess
{
    private readonly Process _process;

    public ProcessSandboxChildProcess(Process process)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
    }

    public int Id => _process.Id;

    public Stream StandardInput => _process.StandardInput.BaseStream;

    public Stream StandardOutput => _process.StandardOutput.BaseStream;

    public bool TimedOut => false;

    public int? ExitCode
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                // Disposed, or never started: there is no exit code to report.
                return null;
            }
        }
    }

    public IReadOnlyList<string> Warnings => [];

    public async Task WriteStandardInputAndCloseAsync(string text, CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken);
        _process.StandardInput.Close();
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        // Also waits for the async output pump to drain, so the captured builders are complete once it returns.
        await _process.WaitForExitAsync(cancellationToken);
        return _process.ExitCode;
    }

    public async Task<bool> TryWaitForExitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _process.WaitForExitAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    public void Kill() =>
        SandboxProcessTree.TreeKill(_process);

    public void Dispose() =>
        _process.Dispose();
}
