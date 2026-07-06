namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Diagnostics;
using System.Runtime.Versioning;

/// <summary>
///     Run-to-completion streaming process runner for the in-app CUDA build (NEW code — the supervised
///     <see cref="LlamaServerProcessLauncher" /> is a long-lived-handle launcher, not this shape). Spawns a tool by argv
///     (no shell) under <c>setsid -w</c> so the child runs in a NEW session/process group (<c>kill(-pgid)</c> reaps the
///     whole tree); streams stdout+stderr line-by-line to a sink as the build runs; awaits exit; and tree-kills the
///     whole process group (reusing <see cref="LinuxProcessGroupHandle" />) on cancellation or timeout. <c>[archMED-3]</c>
/// </summary>
/// <remarks>
///     The child's environment is the SCRUBBED, allowlisted dictionary the caller supplies — the runner replaces the
///     inherited environment entirely (<see cref="ProcessStartInfo.Environment" /> cleared, then the allowlist applied),
///     so the build never inherits <c>LD_PRELOAD</c>, compiler-launcher, git-transport, or app-secret variables.
///     <c>[secHIGH-2]</c> Linux-only (the in-app build targets Linux).
/// </remarks>
[SupportedOSPlatform("linux")]
internal static class StreamingProcessRunner
{
    /// <summary>
    ///     Runs <paramref name="file" /> <paramref name="args" /> under <c>setsid -w</c> in
    ///     <paramref name="workingDirectory" /> with the scrubbed <paramref name="environment" />, streaming each output
    ///     line to <paramref name="logSink" />. Returns the process exit code on normal completion.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct" /> fired (the group is tree-killed first).</exception>
    /// <exception cref="LlamaRuntimeException">The process failed to start, or exceeded <paramref name="timeout" /> (group tree-killed).</exception>
    public static async Task<int> RunAsync(string file,
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
            // setsid runs the child in a NEW session/process group (pgid == child pid), so kill(-pgid) reaps the whole
            // build tree. setsid execs the program in place — it only forks when this process is already a group
            // leader — and -w makes it wait for and propagate the program's exit status in that edge case. Behavior
            // is otherwise unchanged.
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

        // Scrubbed env: replace the inherited environment entirely with the caller's allowlist. [secHIGH-2]
        startInfo.Environment.Clear();
        foreach (var entry in environment)
        {
            startInfo.Environment[entry.Key] = entry.Value;
        }

        // The handle takes ownership of the started process: its Dispose tree-kills the group and disposes the process.
        // StartStreaming disposes the process on a start failure, so ownership is cleanly transferred to the handle.
#pragma warning disable CA2000 // Ownership transferred to the handle (Wrap disposes on a construction failure); the using disposes the handle.
        using var handle = LinuxProcessGroupHandle.Wrap(StartStreaming(startInfo, logSink));
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
            throw new LlamaRuntimeException("The build step exceeded its time budget and was stopped.");
        }
    }

    // Creates, wires line-forwarding on, and starts the process; disposes it (transferring no ownership) on any start
    // failure. On success ownership is transferred to the caller (which wraps it in a LinuxProcessGroupHandle).
    private static Process StartStreaming(ProcessStartInfo startInfo, Action<string> logSink)
    {
        var process = new Process
        {
            StartInfo = startInfo
        };
        process.OutputDataReceived += (_, e) => Forward(e.Data, logSink);
        process.ErrorDataReceived += (_, e) => Forward(e.Data, logSink);

        try
        {
            if (!process.Start())
            {
                throw new LlamaRuntimeException("The build process did not start.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
        catch (LlamaRuntimeException)
        {
            process.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new LlamaRuntimeException("The build process could not be started.", ex);
        }
    }

    private static void Forward(string? line, Action<string> logSink)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        logSink(line);
    }
}
