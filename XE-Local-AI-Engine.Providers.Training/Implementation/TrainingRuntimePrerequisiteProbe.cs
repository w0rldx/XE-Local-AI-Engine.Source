namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using System.Globalization;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     Reports, per item, whether this machine can provision the Python training runtime. Read-only — it never creates
///     or mutates the cache root, so the UI can call it before the operator commits to a multi-gigabyte install.
/// </summary>
/// <remarks>
///     GPU presence is probed with <c>nvidia-smi</c> rather than by looking for <c>/dev/nvidia*</c> or
///     <c>/proc/driver/nvidia/version</c>: under WSL2 — a supported host — neither exists, and the
///     only evidence of a working driver is the shim at <c>/usr/lib/wsl/lib/nvidia-smi</c>. A device-node check would
///     report "no GPU" on a machine with a working RTX 5090.
/// </remarks>
internal sealed class TrainingRuntimePrerequisiteProbe : ITrainingRuntimePrerequisiteProbe
{
    // Peak disk during an install is roughly two copies of the venv: the staged one being built plus the previous one
    // parked in backup until the swap succeeds. A measured venv is ~7.5 GB, so 20 GB leaves honest headroom.
    internal const long RequiredFreeDiskBytes = 20L * 1024 * 1024 * 1024;

    // QLoRA fine-tuning stages optimizer state and dataset shards through host RAM; below this the run thrashes.
    internal const long RequiredSystemMemoryBytes = 16L * 1024 * 1024 * 1024;

    private static readonly TimeSpan NvidiaSmiTimeout = TimeSpan.FromSeconds(20);

    // The one free-disk measurement in the node; see DriveInfoFreeSpaceProbe for why a path root is the wrong input.
    private static readonly IFreeSpaceProbe FreeSpace = new DriveInfoFreeSpaceProbe();
    private readonly string _cacheRoot;
    private readonly ITrainingProcessRunner _processRunner;
    private readonly string _scriptsDirectory;

    public TrainingRuntimePrerequisiteProbe(ITrainingProcessRunner processRunner, string cacheRoot, string scriptsDirectory)
    {
        _cacheRoot = !string.IsNullOrWhiteSpace(cacheRoot)
            ? cacheRoot
            : throw new ArgumentException("The cache root is required.", nameof(cacheRoot));
        ArgumentNullException.ThrowIfNull(processRunner);
        _processRunner = processRunner;
        _scriptsDirectory = !string.IsNullOrWhiteSpace(scriptsDirectory)
            ? scriptsDirectory
            : throw new ArgumentException("The scripts directory is required.", nameof(scriptsDirectory));
    }

    public async Task<TrainingRuntimePrerequisiteReport> ProbeAsync(CancellationToken ct)
    {
        var items = new List<TrainingRuntimePrerequisiteItem>
        {
            ProbePlatform(),
            ProbeDisk(),
            ProbeMemory(),
            ProbeLockfile()
        };

        // Only worth spawning a process once the platform gate passed; on Windows nvidia-smi would be a different
        // binary answering a question the platform item has already refused.
        items.Add(OperatingSystem.IsLinux()
            ? await ProbeNvidiaDriverAsync(ct).ConfigureAwait(false)
            : new TrainingRuntimePrerequisiteItem
            {
                Key = TrainingRuntimePrerequisiteKeys.NvidiaDriver,
                Satisfied = false,
                Detail = "The NVIDIA driver was not checked because training is available on Linux only."
            });

        return new TrainingRuntimePrerequisiteReport
        {
            CanInstall = items.TrueForAll(static item => item.Satisfied),
            Items = items
        };
    }

    private static TrainingRuntimePrerequisiteItem ProbePlatform()
    {
        return OperatingSystem.IsLinux()
            ? new TrainingRuntimePrerequisiteItem
            {
                Key = TrainingRuntimePrerequisiteKeys.Platform,
                Satisfied = true,
                Detail = "Running on Linux."
            }
            : new TrainingRuntimePrerequisiteItem
            {
                Key = TrainingRuntimePrerequisiteKeys.Platform,
                Satisfied = false,
                Detail = "Training is available on Linux only."
            };
    }

    private TrainingRuntimePrerequisiteItem ProbeDisk()
    {
        var required = FormatGigabytes(RequiredFreeDiskBytes);
        try
        {
            var available = FreeSpace.GetAvailableFreeBytes(_cacheRoot);
            return available >= RequiredFreeDiskBytes
                ? new TrainingRuntimePrerequisiteItem
                {
                    Key = TrainingRuntimePrerequisiteKeys.FreeDisk,
                    Satisfied = true,
                    Detail = $"{FormatGigabytes(available)} free ({required} required)."
                }
                : new TrainingRuntimePrerequisiteItem
                {
                    Key = TrainingRuntimePrerequisiteKeys.FreeDisk,
                    Satisfied = false,
                    Detail = $"Only {FormatGigabytes(available)} free; {required} is required."
                };
        }
        // InvalidOperationException is the shared probe's "nothing exists at or above the cache root", which is the
        // same "could not be determined" row the null-ancestor branch reported.
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return new TrainingRuntimePrerequisiteItem
            {
                Key = TrainingRuntimePrerequisiteKeys.FreeDisk,
                Satisfied = false,
                Detail = "The free disk space could not be determined."
            };
        }
    }

    private static TrainingRuntimePrerequisiteItem ProbeMemory()
    {
        var required = FormatGigabytes(RequiredSystemMemoryBytes);
        var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (total <= 0)
        {
            return new TrainingRuntimePrerequisiteItem
            {
                Key = TrainingRuntimePrerequisiteKeys.SystemMemory,
                Satisfied = false,
                Detail = "The installed system memory could not be determined."
            };
        }

        return total >= RequiredSystemMemoryBytes
            ? new TrainingRuntimePrerequisiteItem
            {
                Key = TrainingRuntimePrerequisiteKeys.SystemMemory,
                Satisfied = true,
                Detail = $"{FormatGigabytes(total)} of system memory ({required} required)."
            }
            : new TrainingRuntimePrerequisiteItem
            {
                Key = TrainingRuntimePrerequisiteKeys.SystemMemory,
                Satisfied = false,
                Detail = $"Only {FormatGigabytes(total)} of system memory; {required} is required."
            };
    }

    private TrainingRuntimePrerequisiteItem ProbeLockfile()
    {
        var lockfile = Path.Combine(_scriptsDirectory, TrainingRuntimeLayout.LockfileName);
        var project = Path.Combine(_scriptsDirectory, TrainingRuntimeLayout.ProjectFileName);
        var probe = Path.Combine(_scriptsDirectory, TrainingRuntimeLayout.ProbeScriptName);
        if (File.Exists(lockfile) && File.Exists(project) && File.Exists(probe))
        {
            return new TrainingRuntimePrerequisiteItem
            {
                Key = TrainingRuntimePrerequisiteKeys.Lockfile,
                Satisfied = true,
                Detail = "The pinned training runtime lockfile is present."
            };
        }

        return new TrainingRuntimePrerequisiteItem
        {
            Key = TrainingRuntimePrerequisiteKeys.Lockfile,
            Satisfied = false,
            Detail = "The pinned training runtime lockfile is missing from this installation."
        };
    }

    private async Task<TrainingRuntimePrerequisiteItem> ProbeNvidiaDriverAsync(CancellationToken ct)
    {
        var lines = new List<string>();
        try
        {
            // The app's own base directory: owned by this installation, guaranteed to exist, and not world-writable —
            // the probe must not create anything under the cache root, so it borrows a directory instead.
            var exitCode = await _processRunner.RunAsync("nvidia-smi",
                ["--query-gpu=driver_version,name", "--format=csv,noheader"],
                TrainingRuntimeEnvironment.BuildProbeEnvironment(AppContext.BaseDirectory),
                AppContext.BaseDirectory,
                line => lines.Add(line),
                NvidiaSmiTimeout,
                ct).ConfigureAwait(false);

            if (exitCode != 0 || lines.Count == 0)
            {
                return new TrainingRuntimePrerequisiteItem
                {
                    Key = TrainingRuntimePrerequisiteKeys.NvidiaDriver,
                    Satisfied = false,
                    Detail = "No NVIDIA driver was detected. Training requires a CUDA-capable GPU."
                };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is TrainingRuntimeException or IOException or UnauthorizedAccessException)
        {
            return new TrainingRuntimePrerequisiteItem
            {
                Key = TrainingRuntimePrerequisiteKeys.NvidiaDriver,
                Satisfied = false,
                Detail = "No NVIDIA driver was detected. Training requires a CUDA-capable GPU."
            };
        }

        // "999.99, NVIDIA GeForce RTX 5090" — reported back verbatim; it names hardware, not a path or a secret.
        return new TrainingRuntimePrerequisiteItem
        {
            Key = TrainingRuntimePrerequisiteKeys.NvidiaDriver,
            Satisfied = true,
            Detail = $"NVIDIA driver detected: {lines[0].Trim()}."
        };
    }

    private static string FormatGigabytes(long bytes)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1024 * 1024 * 1024):0.#} GB");
    }
}
