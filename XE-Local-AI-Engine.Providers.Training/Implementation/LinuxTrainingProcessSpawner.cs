namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Channels;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     Spawn-and-return trainer launcher. <see cref="LinuxTrainingProcessRunner" /> is run-to-completion and serves the
///     installer; a training run instead needs the child's identity the instant it exists, because the launch receipt
///     has to be durable before the first byte of output arrives — a host that dies between spawn and receipt leaves an
///     unreapable orphan holding the whole GPU.
/// </summary>
internal sealed class LinuxTrainingProcessSpawner(string? cacheRoot = null) : ITrainingProcessSpawner
{
    private readonly string _cacheRoot = string.IsNullOrWhiteSpace(cacheRoot) ? TrainingRuntimeLayout.DefaultCacheRoot() : cacheRoot;

    public ITrainingProcessHandle Spawn(TrainingSpawnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Arguments is null
            || string.IsNullOrWhiteSpace(request.ExecutablePath)
            || string.IsNullOrWhiteSpace(request.WorkingDirectory)
            || string.IsNullOrWhiteSpace(request.RunToken))
        {
            throw new ArgumentException("A trainer spawn needs an executable, arguments, a working directory and a run token.", nameof(request));
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new TrainingRuntimeException("The Python training runtime is available on Linux only.");
        }

        return SpawnLinux(request, _cacheRoot);
    }

    [SupportedOSPlatform("linux")]
    private static ITrainingProcessHandle SpawnLinux(TrainingSpawnRequest request, string cacheRoot)
    {
        foreach (var directory in TrainingRuntimeEnvironment.TrainEnvironmentDirectories(cacheRoot, request.WorkingDirectory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        var startInfo = new ProcessStartInfo
        {
            // setsid puts the child in its own session and process group, so kill(-pgid) reaps the trainer plus every
            // dataloader worker and compile subprocess it forked. It does not change the child's PPID, so the child
            // stays inside dev-stop's parent-chain descendant closure.
            FileName = SetsidLocator.ResolveAbsolutePath(),
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-w");
        startInfo.ArgumentList.Add(request.ExecutablePath);
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Scrubbed env: the inherited environment is dropped entirely and replaced by the provider-owned allowlist, so
        // a training run never inherits LD_PRELOAD, proxy or credential variables, or any node secret.
        startInfo.Environment.Clear();
        var environment = request.GgufPyDirectory is { Length: > 0 } ggufPy
            ? TrainingRuntimeEnvironment.BuildExportEnvironment(cacheRoot, request.WorkingDirectory, ggufPy)
            : TrainingRuntimeEnvironment.BuildTrainEnvironment(cacheRoot, request.WorkingDirectory);
        foreach (var entry in environment)
        {
            startInfo.Environment[entry.Key] = entry.Value;
        }

        startInfo.Environment[LinuxTrainingProcessInspector.RunTokenVariable] = request.RunToken;

        // ponytail: unbounded. The reader is the stdio parser, which coalesces its own database writes, so it never
        // stalls behind the trainer. Bound it if a future consumer does per-line I/O.
        var output = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

#pragma warning disable CA2000 // Ownership transfers to the handle, which disposes the process; Start disposes on failure.
        var process = StartStreaming(startInfo, output.Writer);
#pragma warning restore CA2000
        try
        {
            // Identity is read from /proc rather than assumed: setsid execs in place in the common case (pgid == pid)
            // but forks when this host already leads a session, and every guarantee the reaper makes rests on the
            // recorded pgid being the one that will actually be signalled.
            var stat = LinuxTrainingProcessInspector.TryReadStat(process.Id);
            var receipt = new TrainingLaunchReceipt(process.Id,
                stat?.Pgid ?? process.Id,
                LinuxTrainingProcessInspector.ResolveExecutablePath(process.Id),
                stat?.StartTicks ?? 0,
                request.RunToken);
            return new LinuxTrainingProcessHandle(process, receipt, output);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static Process StartStreaming(ProcessStartInfo startInfo, ChannelWriter<string> writer)
    {
        var process = new Process
        {
            StartInfo = startInfo
        };

        // stdout and stderr close independently; the merged stream is only complete once both have.
        var streamsClosed = 0;

        void Complete()
        {
            if (Interlocked.Increment(ref streamsClosed) == 2)
            {
                _ = writer.TryComplete();
            }
        }

        process.OutputDataReceived += (_, e) => Forward(e.Data, writer, Complete);
        process.ErrorDataReceived += (_, e) => Forward(e.Data, writer, Complete);

        try
        {
            if (!process.Start())
            {
                throw new TrainingRuntimeException("The trainer process did not start.");
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
        catch (Exception exception)
        {
            process.Dispose();
            throw new TrainingRuntimeException("The trainer process could not be started.", exception);
        }
    }

    private static void Forward(string? line, ChannelWriter<string> writer, Action onClosed)
    {
        if (line is null)
        {
            // A null payload is the stream's end-of-file marker, not an empty line.
            onClosed();
            return;
        }

        _ = writer.TryWrite(line);
    }
}
