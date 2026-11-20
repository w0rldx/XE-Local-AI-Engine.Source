namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.ComponentModel;
using System.Runtime.InteropServices;

/// <summary>A running command and the Job Object that holds it and everything it starts.</summary>
/// <remarks>
///     <see cref="Terminate" /> ends every member; <see cref="Dispose" /> terminates, then closes the kill-on-close job and every handle. The
///     engine holds the only job handle, so engine death kills the members too.
/// </remarks>
public sealed class WindowsJobProcess : IDisposable
{
    private readonly IWindowsJobApi _api;
    private readonly WindowsJobChild _child;
    private readonly Task _exited;
    private readonly SafeHandle _job;
    private int? _exitCode;

    internal WindowsJobProcess(IWindowsJobApi api, SafeHandle job, WindowsJobChild child)
    {
        _api = api;
        _job = job;
        _child = child;
        _exited = api.WaitForExitAsync(child.Process);
    }

    public int Id => _child.ProcessId;

    /// <summary>The write end of the child's stdin; disposing it sends end of stream.</summary>
    public Stream StandardInput => _child.StandardInput;

    public Stream StandardOutput => _child.StandardOutput;

    public Stream StandardError => _child.StandardError;

    /// <summary>The root process's exit code once it has exited, else <see langword="null" />. Never waits.</summary>
    public int? ExitCode
    {
        get
        {
            if (_exitCode is null && _exited.IsCompletedSuccessfully && !_child.Process.IsClosed)
            {
                _exitCode = _api.GetExitCode(_child.Process);
            }

            return _exitCode;
        }
    }

    /// <summary>Completes when the root process has exited; members it started may still run until <see cref="Terminate" />.</summary>
    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _exited.WaitAsync(cancellationToken);

    /// <summary>Ends every member of the job. Never throws.</summary>
    public void Terminate()
    {
        try
        {
            _api.Terminate(_job);
        }
        catch (Exception exception) when (exception is Win32Exception or ObjectDisposedException)
        {
            // Already terminated or closed: closing the kill-on-close job is the backstop either way.
        }
    }

    public void Dispose()
    {
        try
        {
            // Read while the process handle is open, so ExitCode still answers after Dispose.
            _ = ExitCode;
        }
        catch (Win32Exception)
        {
            // The exit code stays unknown; the teardown below must run regardless.
        }

        Terminate();
        try
        {
            _child.Dispose();
        }
        finally
        {
            // The kill-on-close job is the backstop for every member: it is closed even if a pipe end fails to close.
            _job.Dispose();
        }
    }

    /// <summary>The job handle, for the Windows tests' membership and read-back checks.</summary>
    internal SafeHandle Job => _job;

    /// <summary>The probe's commit control: commits and frees <paramref name="bytes" /> inside the job.</summary>
    internal bool TryCommit(ulong bytes, out int error) =>
        _api.TryCommit(_child.Process, bytes, out error);
}
