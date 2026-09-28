namespace XE_Local_AI_Engine.Providers.Python.Implementation;

using System.Diagnostics;
using System.Runtime.Versioning;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.Python.Contracts;

/// <summary>
///     The production <see cref="IPythonToolRunner" />: spawns a tool by argv (no shell) under <c>setsid -w</c>,
///     streams stdout and stderr line-by-line to a sink, awaits exit, and tree-kills on cancellation or timeout.
/// </summary>
/// <remarks>
///     <c>setsid</c> puts the child in a NEW session and process group so <c>kill(-pgid)</c> reaps the whole tree. The
///     child's environment is the SCRUBBED, allowlisted dictionary the caller supplies, with the inherited environment
///     cleared entirely first. Mirrors <c>StreamingProcessRunner</c> in the LlamaServer provider, which cannot be
///     reused: it is internal to that assembly and this project references <c>Providers.Abstractions</c> only
///     (ADR 0005 decision 3).
/// </remarks>
public sealed class LinuxPythonToolRunner : IPythonToolRunner
{
    public Task<int> RunAsync(string file,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environment,
        string workingDirectory,
        Action<string> logSink,
        TimeSpan timeout,
        CancellationToken ct)
    {
        // The platform gate lives here rather than as a type-level attribute so the DI factory can construct the runner
        // unconditionally; every caller already refuses on non-Linux well before reaching a subprocess.
        if (!OperatingSystem.IsLinux())
        {
            throw new ManagedPythonException("Managed Python tooling is available on Linux only.");
        }

        return RunLinuxAsync(file, args, environment, workingDirectory, logSink, timeout, ct);
    }

    [SupportedOSPlatform("linux")]
    private static async Task<int> RunLinuxAsync(string file,
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
            // setsid execs in place unless this process is already a group leader; -w makes it wait for and propagate
            // the program's exit status in that forking edge case.
            FileName = SetsidLocator.ResolveAbsolutePath(),
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-w");
        startInfo.ArgumentList.Add(file);
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

#pragma warning disable CA2000 // Ownership transferred to the handle (Wrap disposes on a construction failure); the using disposes the handle.
        using var handle = LinuxProcessGroupHandle.Wrap(PythonToolRunner.StartStreaming(startInfo, logSink));
#pragma warning restore CA2000
        var process = handle.Process;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            handle.TreeKill();
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            handle.TreeKill();
            throw new ManagedPythonException("A managed Python step exceeded its time budget and was stopped.");
        }
    }
}
