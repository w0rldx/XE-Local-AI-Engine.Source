namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="ILlamaCppSourceBuildPrerequisiteProbe" />. Reports — never installs — the toolchain an in-app CUDA
///     <c>llama-server</c> build needs. Each tool item spawns the tool with a version/probe argument (bounded, tree-killed,
///     degrade-never-throw — modeled on <see cref="ProcessGpuVendorProbe" />); the NVIDIA-GPU item reuses
///     <see cref="IGpuVendorProbe" />; the free-disk item checks the build cache root's drive. Non-Linux short-circuits to
///     a single unsatisfied OS item with <see cref="LlamaCppSourceBuildPrerequisiteReport.CanBuild" /> false.
/// </summary>
/// <remarks>
///     The probe touches no command with host-derived data and surfaces only sanitized, user-safe detail strings (a
///     trimmed first version line or a fixed reason) — never an absolute path, URL, or secret.
/// </remarks>
public sealed class LlamaCppSourceBuildPrerequisiteProbe : ILlamaCppSourceBuildPrerequisiteProbe
{
    // Hard cap per probed tool, mirroring ProcessGpuVendorProbe: a hung tool can never block the checklist past this.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    ///     Conservative free-disk floor for a CUDA source build. The clone + the cmake/CUDA object tree for a single
    ///     <c>llama-server</c> target comfortably fits in this; the check is a disk-exhaustion guard, not a precise
    ///     estimate. Re-checked again immediately before the build starts (the probe value can go stale).
    /// </summary>
    internal const long RequiredFreeDiskBytes = 15L * 1024 * 1024 * 1024;

    private readonly string _buildCacheRoot;
    private readonly long _requiredFreeDiskBytes;
    private readonly IGpuVendorProbe _vendorProbe;

    /// <summary>Creates the probe over the supplied vendor probe, defaulting the disk check to the shared app cache root.</summary>
    public LlamaCppSourceBuildPrerequisiteProbe(IGpuVendorProbe vendorProbe)
        : this(vendorProbe, DefaultCacheRoot(), RequiredFreeDiskBytes)
    {
    }

    /// <summary>Test seam: pins the cache root whose drive the free-disk item inspects, and the required-free-disk floor.</summary>
    internal LlamaCppSourceBuildPrerequisiteProbe(IGpuVendorProbe vendorProbe, string buildCacheRoot, long requiredFreeDiskBytes = RequiredFreeDiskBytes)
    {
        _vendorProbe = vendorProbe ?? throw new ArgumentNullException(nameof(vendorProbe));
        ArgumentException.ThrowIfNullOrWhiteSpace(buildCacheRoot);
        _buildCacheRoot = buildCacheRoot;
        _requiredFreeDiskBytes = requiredFreeDiskBytes;
    }

    /// <inheritdoc />
    public async Task<LlamaCppSourceBuildPrerequisiteReport> ProbeAsync(LlamaCppSourceBackend backend, CancellationToken ct)
    {
        // Non-Linux is a hard gate: the in-app build targets Linux only. Report a single unsatisfied OS item and stop —
        // probing toolchain versions on Windows/macOS would be misleading (the build never runs there).
        if (!OperatingSystem.IsLinux())
        {
            var osOnly = new[]
            {
                new LlamaCppSourceBuildPrerequisiteItem("os-is-linux", Satisfied: false, "In-app source builds are available on Linux only.")
            };
            return new LlamaCppSourceBuildPrerequisiteReport(CanBuild: false, osOnly);
        }

        var items = new List<LlamaCppSourceBuildPrerequisiteItem>
        {
            new("os-is-linux", Satisfied: true, "Linux host detected."),
            await ProbeToolAsync("cmake", ["--version"], "CMake", ct).ConfigureAwait(false),
            await ProbeToolAsync("gcc", ["--version"], "C compiler (gcc)", ct).ConfigureAwait(false),
            await ProbeToolAsync("g++", ["--version"], "C++ compiler (g++)", ct).ConfigureAwait(false),
            await ProbeBuildToolAsync(ct).ConfigureAwait(false),
            await ProbeToolAsync("git", ["--version"], "git", ct).ConfigureAwait(false),
            ProbeFreeDisk()
        };

        if (backend == LlamaCppSourceBackend.Cuda)
        {
            var nvidiaPresent = await DetectNvidiaAsync(ct).ConfigureAwait(false);
            items.Insert(1, new LlamaCppSourceBuildPrerequisiteItem("nvidia-gpu", nvidiaPresent,
                nvidiaPresent ? "NVIDIA GPU/driver detected." : "No NVIDIA GPU or driver detected."));
            items.Insert(2, await ProbeToolAsync("nvcc", ["--version"], "NVIDIA CUDA compiler (nvcc)", ct).ConfigureAwait(false));
            items.Insert(3, await ProbeToolAsync("nvidia-smi", ["--query-gpu=compute_cap", "--format=csv,noheader"], "NVIDIA driver probe", ct).ConfigureAwait(false));
        }
        else if (backend == LlamaCppSourceBackend.Vulkan)
        {
            items.Insert(1, await ProbeToolAsync("glslc", ["--version"], "Vulkan shader compiler (glslc)", ct).ConfigureAwait(false));
            items.Insert(2, await ProbeToolAsync("vulkaninfo", ["--summary"], "Vulkan runtime probe", ct).ConfigureAwait(false));
        }

        var canBuild = items.TrueForAll(static item => item.Satisfied);
        return new LlamaCppSourceBuildPrerequisiteReport(canBuild, items);
    }

    private async Task<bool> DetectNvidiaAsync(CancellationToken ct)
    {
        try
        {
            var vendor = await _vendorProbe.DetectVendorAsync(ct).ConfigureAwait(false);
            return vendor == DetectedGpuVendor.Nvidia;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A vendor-probe failure must not fail the whole checklist; treat as "not detected".
            return false;
        }
    }

    // make OR ninja satisfies the build-generator requirement; report which (if any) was found.
    private static async Task<LlamaCppSourceBuildPrerequisiteItem> ProbeBuildToolAsync(CancellationToken ct)
    {
        var make = await TryProbeAsync("make", ["--version"], ct).ConfigureAwait(false);
        if (make is { Length: > 0 })
        {
            return new LlamaCppSourceBuildPrerequisiteItem("make-or-ninja", Satisfied: true, "make detected.");
        }

        var ninja = await TryProbeAsync("ninja", ["--version"], ct).ConfigureAwait(false);
        if (ninja is { Length: > 0 })
        {
            return new LlamaCppSourceBuildPrerequisiteItem("make-or-ninja", Satisfied: true, "ninja detected.");
        }

        return new LlamaCppSourceBuildPrerequisiteItem("make-or-ninja", Satisfied: false, "Neither make nor ninja was found.");
    }

    private static async Task<LlamaCppSourceBuildPrerequisiteItem> ProbeToolAsync(string fileName, IReadOnlyList<string> args, string displayName, CancellationToken ct)
    {
        var banner = await TryProbeAsync(fileName, args, ct).ConfigureAwait(false);
        return banner is { Length: > 0 }
            ? new LlamaCppSourceBuildPrerequisiteItem(fileName, Satisfied: true, $"{displayName} detected: {banner}")
            : new LlamaCppSourceBuildPrerequisiteItem(fileName, Satisfied: false, $"{displayName} was not found on PATH.");
    }

    // Spawns `<fileName> <args>` (no shell, argv only), bounded + tree-killed, and returns the trimmed first stdout/stderr
    // line on a clean exit, or null on any failure (missing tool, non-zero, timeout). Mirrors ProcessGpuVendorProbe.
    private static async Task<string?> TryProbeAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };
        try
        {
            if (!process.Start())
            {
                return null;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ProbeTimeout);
            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    return null;
                }

                var combined = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
                return FirstLine(combined);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Tool missing / not on PATH / permission denied — treat as "not present", never fatal.
            return null;
        }
        finally
        {
            ProcessCaptureRunner.TryKill(process);
        }
    }

    private LlamaCppSourceBuildPrerequisiteItem ProbeFreeDisk()
    {
        try
        {
            // The build cache root may not exist yet on a fresh node; walk up to the nearest existing ancestor so
            // DriveInfo resolves the real mount the build will write into.
            var probePath = NearestExistingAncestor(_buildCacheRoot);
            var drive = new DriveInfo(Path.GetPathRoot(probePath) ?? probePath);
            var freeBytes = drive.AvailableFreeSpace;
            var satisfied = freeBytes >= _requiredFreeDiskBytes;
            var freeGb = freeBytes / (1024.0 * 1024 * 1024);
            var requiredGb = _requiredFreeDiskBytes / (1024.0 * 1024 * 1024);
            return new LlamaCppSourceBuildPrerequisiteItem("free-disk",
                satisfied,
                satisfied
                    ? $"{freeGb:F1} GB free (need {requiredGb:F0} GB)."
                    : $"Only {freeGb:F1} GB free; {requiredGb:F0} GB required.");
        }
        catch (Exception)
        {
            // A disk query failure must not throw out of the probe; report it as unsatisfied so the build stays gated.
            return new LlamaCppSourceBuildPrerequisiteItem("free-disk", Satisfied: false, "Free disk space could not be determined.");
        }
    }

    private static string NearestExistingAncestor(string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }

        return string.IsNullOrEmpty(current) ? path : current;
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var newline = text.IndexOfAny(['\r', '\n']);
        var line = newline < 0 ? text : text[..newline];
        return line.Trim();
    }

    private static string DefaultCacheRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XE-Local-AI-Engine");
    }
}
