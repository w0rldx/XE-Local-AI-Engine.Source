namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using System.Diagnostics;
using System.Runtime.Versioning;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The production <see cref="ITrainingProcessRunner" />. Spawns a tool by argv (no shell) under <c>setsid -w</c> so
///     the child runs in a NEW session/process group and <c>kill(-pgid)</c> reaps the whole tree; streams stdout+stderr
///     line-by-line to a sink as the install runs; awaits exit; and tree-kills the group on cancellation or timeout.
/// </summary>
/// <remarks>
///     The child's environment is the SCRUBBED, allowlisted dictionary the caller supplies — the inherited environment is
///     cleared entirely first, so a uv install never inherits <c>LD_PRELOAD</c>, proxy/credential variables, or any node
///     secret. This mirrors <c>StreamingProcessRunner</c> in the LlamaServer provider, which cannot be reused directly:
///     it is internal to that assembly and this project references <c>Providers.Abstractions</c> only (ADR 0005 §3).
/// </remarks>
internal sealed class LinuxTrainingProcessRunner : ITrainingProcessRunner
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
            throw new TrainingRuntimeException("The Python training runtime is available on Linux only.");
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
        using var handle = LinuxTrainingProcessGroupHandle.Wrap(StartStreaming(startInfo, logSink));
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
            throw new TrainingRuntimeException("The training runtime install step exceeded its time budget and was stopped.");
        }
    }

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
                throw new TrainingRuntimeException("The training runtime install process did not start.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
        catch (TrainingRuntimeException)
        {
            process.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new TrainingRuntimeException("The training runtime install process could not be started.", ex);
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
