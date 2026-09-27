namespace XE_Local_AI_Engine.Providers.Python.Implementation;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using XE_Local_AI_Engine.Providers.Python.Contracts;

/// <summary>
///     The Windows <see cref="IPythonToolRunner" />: spawns a tool by argv (no shell) under the caller's scrubbed
///     environment, streams stdout and stderr line-by-line, and tree-kills on cancellation or timeout.
/// </summary>
/// <remarks>
///     <c>Process.Kill(entireProcessTree: true)</c> suffices: uv and the venv interpreter are binaries the engine chose, so
///     no Job Object is needed to contain an untrusted child (ADR 0016). A venv's <c>python.exe</c> is a launcher that
///     re-spawns the base interpreter, which the tree kill reaches too.
/// </remarks>
public sealed class WindowsPythonToolRunner : IPythonToolRunner
{
    public Task<int> RunAsync(string file,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environment,
        string workingDirectory,
        Action<string> logSink,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ManagedPythonException("This managed Python runner is available on Windows only.");
        }

        return RunWindowsAsync(file, args, environment, workingDirectory, logSink, timeout, ct);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunWindowsAsync(string file,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environment,
        string workingDirectory,
        Action<string> logSink,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(logSink);

        var startInfo = new ProcessStartInfo
        {
            FileName = file,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // Scrubbed env: replace the inherited environment entirely with the caller's allowlist.
        startInfo.Environment.Clear();
        foreach (var entry in environment)
        {
            startInfo.Environment[entry.Key] = entry.Value;
        }

        using var process = PythonToolRunner.StartStreaming(startInfo, logSink);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TreeKillAsync(process).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            await TreeKillAsync(process).ConfigureAwait(false);
            throw new ManagedPythonException("A managed Python step exceeded its time budget and was stopped.");
        }
    }

    private static async Task TreeKillAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or AggregateException)
        {
            // Already exited, or a child exited or refused between the snapshot and the kill (AggregateException).
        }

        // Bounded: TerminateProcess is asynchronous, and the caller should not see the step end while uv still exits.
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Still exiting after the bound; the cancellation or timeout surfaces regardless.
        }
    }
}
