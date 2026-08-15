namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using System.Globalization;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     Reports, per item, whether this machine can provision the Python training runtime. Read-only — it never creates
///     or mutates the cache root, so the UI can call it before the operator commits to a multi-gigabyte install.
/// </summary>
/// <remarks>
///     GPU presence is probed with <c>nvidia-smi</c> rather than by looking for <c>/dev/nvidia*</c> or
///     <c>/proc/driver/nvidia/version</c>: under WSL2 — which is where this box actually runs — neither exists, and the
///     only evidence of a working driver is the shim at <c>/usr/lib/wsl/lib/nvidia-smi</c>. A device-node check would
///     report "no GPU" on a machine with a working RTX 5090.
/// </remarks>
internal sealed class TrainingRuntimePrerequisiteProbe(ITrainingProcessRunner processRunner, string cacheRoot, string scriptsDirectory)
    : ITrainingRuntimePrerequisiteProbe
{
    // Peak disk during an install is roughly two copies of the venv: the staged one being built plus the previous one
    // parked in backup until the swap succeeds. A measured venv is ~7.5 GB, so 20 GB leaves honest headroom.
    internal const long RequiredFreeDiskBytes = 20L * 1024 * 1024 * 1024;

    // QLoRA fine-tuning stages optimizer state and dataset shards through host RAM; below this the run thrashes.
    internal const long RequiredSystemMemoryBytes = 16L * 1024 * 1024 * 1024;

    private static readonly TimeSpan NvidiaSmiTimeout = TimeSpan.FromSeconds(20);

    private readonly string _cacheRoot = !string.IsNullOrWhiteSpace(cacheRoot)
        ? cacheRoot
        : throw new ArgumentException("The cache root is required.", nameof(cacheRoot));

    private readonly ITrainingProcessRunner _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));

    private readonly string _scriptsDirectory = !string.IsNullOrWhiteSpace(scriptsDirectory)
        ? scriptsDirectory
        : throw new ArgumentException("The scripts directory is required.", nameof(scriptsDirectory));

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
            : new TrainingRuntimePrerequisiteItem(TrainingRuntimePrerequisiteKeys.NvidiaDriver,
                Satisfied: false,
                "The NVIDIA driver was not checked because training is available on Linux only."));

        return new TrainingRuntimePrerequisiteReport(items.TrueForAll(static item => item.Satisfied), items);
    }

    private static TrainingRuntimePrerequisiteItem ProbePlatform()
    {
        return OperatingSystem.IsLinux()
            ? new TrainingRuntimePrerequisiteItem(TrainingRuntimePrerequisiteKeys.Platform, Satisfied: true, "Running on Linux.")
            : new TrainingRuntimePrerequisiteItem(TrainingRuntimePrerequisiteKeys.Platform,
                Satisfied: false,
                "Training is available on Linux only.");
    }

    private TrainingRuntimePrerequisiteItem ProbeDisk()
    {
        var required = FormatGigabytes(RequiredFreeDiskBytes);
        try
        {
            // The cache root may not exist yet on a first install; measure the nearest existing ancestor, which is the
            // same volume the install will land on.
            var existing = NearestExistingDirectory(_cacheRoot);
            if (existing is null)
            {
                return new TrainingRuntimePrerequisiteItem(TrainingRuntimePrerequisiteKeys.FreeDisk,
                    Satisfied: false,
                    "The free disk space could not be determined.");
            }

            var available = new DriveInfo(existing).AvailableFreeSpace;
            return available >= RequiredFreeDiskBytes
                ? new TrainingRuntimePrerequisiteItem(TrainingRuntimePrerequisiteKeys.FreeDisk,
                    Satisfied: true,
                    $"{FormatGigabytes(available)} free ({required} required).")
                : new TrainingRuntimePrerequisiteItem(TrainingRuntimePrerequisiteKeys.FreeDisk,
                    Satisfied: false,
                    $"Only {FormatGigabytes(available)} free; {required} is required.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new TrainingRuntimePrerequisiteItem(TrainingRuntimePrerequisiteKeys.FreeDisk,
                Satisfied: false,
                "The free disk space could not be determined.");
        }
    }

    private static TrainingRuntimePrerequisiteItem ProbeMemory()
    {
        var required = FormatGigabytes(RequiredSystemMemoryBytes);
        var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (total <= 0)
        {
            return new TrainingRuntimePrerequisiteItem(TrainingRuntimePrerequisiteKeys.SystemMemory,
                Satisfied: false,
                "The installed system memory could not be determined.");
        }

        return total >= RequiredSystemMemoryBytes
            ? new TrainingRuntimePrerequisiteItem(TrainingRuntimePrerequisiteKeys.SystemMemory,
                Satisfied: true,
                $"{FormatGigabytes(total)} of system memory ({required} required).")
            : new TrainingRuntimePrerequisiteItem(TrainingRuntimePrerequisiteKeys.SystemMemory,
                Satisfied: false,
                $"Only {FormatGigabytes(total)} of system memory; {required} is required.");
    }

    private TrainingRuntimePrerequisiteItem ProbeLockfile()
    {
        var lockfile = Path.Combine(_scriptsDirectory, TrainingRuntimeLayout.LockfileName);
        var project = Path.Combine(_scriptsDirectory, TrainingRuntimeLayout.ProjectFileName);
        var probe = Path.Combine(_scriptsDirectory, TrainingRuntimeLayout.ProbeScriptName);
        if (File.Exists(lockfile) && File.Exists(project) && File.Exists(probe))
        {
            return new TrainingRuntimePrerequisiteItem(TrainingRuntimePrerequisiteKeys.Lockfile,
                Satisfied: true,
                "The pinned training runtime lockfile is present.");
        }

        return new TrainingRuntimePrerequisiteItem(TrainingRuntimePrerequisiteKeys.Lockfile,
            Satisfied: false,
            "The pinned training runtime lockfile is missing from this installation.");
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
                return new TrainingRuntimePrerequisiteItem(TrainingRuntimePrerequisiteKeys.NvidiaDriver,
                    Satisfied: false,
                    "No NVIDIA driver was detected. Training requires a CUDA-capable GPU.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is TrainingRuntimeException or IOException or UnauthorizedAccessException)
        {
            return new TrainingRuntimePrerequisiteItem(TrainingRuntimePrerequisiteKeys.NvidiaDriver,
                Satisfied: false,
                "No NVIDIA driver was detected. Training requires a CUDA-capable GPU.");
        }

        // "610.88, NVIDIA GeForce RTX 5090" — reported back verbatim; it names hardware, not a path or a secret.
        return new TrainingRuntimePrerequisiteItem(TrainingRuntimePrerequisiteKeys.NvidiaDriver,
            Satisfied: true,
            $"NVIDIA driver detected: {lines[0].Trim()}.");
    }

    private static string? NearestExistingDirectory(string path)
    {
        var current = Path.GetFullPath(path);
        while (!Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                return null;
            }

            current = parent;
        }

        return current;
    }

    private static string FormatGigabytes(long bytes)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1024 * 1024 * 1024):0.#} GB");
    }
}
