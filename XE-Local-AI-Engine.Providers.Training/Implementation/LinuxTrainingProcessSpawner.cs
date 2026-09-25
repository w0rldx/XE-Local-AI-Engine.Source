namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>Spawn-and-return trainer launcher.</summary>
/// <remarks>
///     <see cref="LinuxTrainingProcessRunner" /> is run-to-completion and serves the installer; a training run instead
///     needs the child's identity the instant it exists, because the launch receipt has to be durable before the first
///     byte of output arrives — a host that dies between spawn and receipt leaves an unreapable orphan holding the
///     whole GPU.
/// </remarks>
internal sealed class LinuxTrainingProcessSpawner : ITrainingProcessSpawner
{
    internal static readonly TimeSpan GroupLeaderTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GroupLeaderPollInterval = TimeSpan.FromMilliseconds(10);

    private readonly string _cacheRoot;
    private readonly ILogger<LinuxTrainingProcessSpawner> _logger;
    private readonly TimeProvider _timeProvider;

    public LinuxTrainingProcessSpawner(TimeProvider timeProvider,
        ILogger<LinuxTrainingProcessSpawner> logger,
        string? cacheRoot = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _timeProvider = timeProvider;
        _logger = logger;
        _cacheRoot = string.IsNullOrWhiteSpace(cacheRoot) ? TrainingRuntimeLayout.DefaultCacheRoot() : cacheRoot;
    }

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

        return SpawnLinux(request);
    }

    [SupportedOSPlatform("linux")]
    private LinuxTrainingProcessHandle SpawnLinux(TrainingSpawnRequest request)
    {
        var cacheRoot = _cacheRoot;
        foreach (var directory in TrainingRuntimeEnvironment.TrainEnvironmentDirectories(cacheRoot, request.WorkingDirectory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        var startInfo = new ProcessStartInfo
        {
            // setsid gives the child its own session and group, so kill(-pgid) reaps its workers too. util-linux forks only
            // when the caller already leads a group; a fresh child does not, so setsid(2) runs in place and keeps pid and PPID.
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

        // simplified: unbounded. The reader is the stdio parser, which coalesces its own database writes, so it never
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
            // Process.Start returns once setsid is exec'd, before it has called setsid(2): until then the child sits in
            // THIS host's group, so the pgid is only trusted once the child leads its own group.
            var launcherPath = ResolveFinalPath(startInfo.FileName);
            var (stat, leadsGroup, executablePath) = AwaitTrainerIdentity(process.Id,
                LinuxTrainingProcessInspector.TryReadStat,
                LinuxTrainingProcessInspector.ResolveExecutablePath,
                launcherPath,
                timeout => process.WaitForExit(timeout),
                _timeProvider);
            if (!leadsGroup)
            {
                _logger.LogWarning("Trainer pid {Pid} did not become its own process-group leader (pgid {Pgid}); it will be signalled by pid only.",
                    process.Id, stat?.Pgid);
            }
            else if (executablePath is null)
            {
                _logger.LogWarning("Trainer pid {Pid} had not exec'd past setsid in time; its receipt records no executable path.", process.Id);
            }

            var receipt = new TrainingLaunchReceipt
            {
                Pid = process.Id,
                Pgid = stat?.Pgid ?? 0,
                ExecutablePath = executablePath,
                StartTicks = stat?.StartTicks ?? 0,
                RunToken = request.RunToken
            };
            return new LinuxTrainingProcessHandle(process, receipt, output, _logger);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Polls <c>/proc</c> until <paramref name="pid" /> leads its own group AND has exec'd past the launcher, the child
    ///     exits, or the timeout passes. setsid(2) runs before the exec, so group leadership alone still reads setsid's path.
    /// </summary>
    /// <param name="waitForExit">Blocks up to the given interval and returns true once the child has exited.</param>
    internal static (TrainingProcessStat? Stat, bool LeadsGroup, string? ExecutablePath) AwaitTrainerIdentity(int pid,
        Func<int, TrainingProcessStat?> readStat,
        Func<int, string?> readExecutable,
        string launcherPath,
        Func<TimeSpan, bool> waitForExit,
        TimeProvider timeProvider)
    {
        var deadline = timeProvider.GetUtcNow() + GroupLeaderTimeout;
        while (true)
        {
            var stat = readStat(pid);
            var leadsGroup = stat is { } current && current.Pgid == pid;
            var executable = readExecutable(pid);
            if (leadsGroup && executable is not null && !string.Equals(executable, launcherPath, StringComparison.Ordinal))
            {
                return (stat, true, executable);
            }

            // Unconfirmed: a path read now may still be setsid's, and pinning it would make the reaper reject the trainer.
            if (stat is null || timeProvider.GetUtcNow() >= deadline || waitForExit(GroupLeaderPollInterval))
            {
                return (stat, leadsGroup, null);
            }
        }
    }

    /// <summary>The symlink-resolved path, matching how <c>/proc/[pid]/exe</c> reports it.</summary>
    private static string ResolveFinalPath(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Path.GetFullPath(path);
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
