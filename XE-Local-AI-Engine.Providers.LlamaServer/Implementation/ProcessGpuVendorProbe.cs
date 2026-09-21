namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default GPU-vendor probe: it prefers a non-shelling driver-presence signal (the NVML runtime library that ships
///     with the NVIDIA display driver) and shells out to lightweight, ubiquitous tools only when that signal is absent.
/// </summary>
/// <remarks>
///     Detection failure degrades to <see cref="DetectedGpuVendor.None" />, the CPU floor, and never throws. Probe
///     order is NVML driver presence (NVIDIA, no process spawned), then <c>nvidia-smi</c> as confirmation, then a
///     platform adapter list for AMD/Intel — <c>lspci</c> on Linux, a <c>Win32_VideoController</c> CIM query on
///     Windows (<see cref="ReadWindowsAdapterListAsync" />). Rationale and the single-timeout model:
///     docs/wiki/03-local-runtime-and-providers.md, "Vendor detection: probe order, the Windows adapter list and the single timeout model".
/// </remarks>
public sealed class ProcessGpuVendorProbe : IGpuVendorProbe
{
    // Hard cap per probe tool: without it a hung tool blocks until the caller's token fires — nvidia-smi can stall indefinitely under some Windows driver/WMI states,
    // and so can a CIM/WMI query against a wedged repository — freezing first-run provisioning. On timeout the tool's whole tree is killed and the vendor reads as undetected.
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(8);

    private readonly Func<bool> _nvidiaDriverPresent;
    private readonly ProbePlatform _platform;
    private readonly TimeSpan _probeTimeout;
    private readonly Func<string, string, IProbeProcess> _processFactory;

    /// <summary>Creates a probe that detects the NVIDIA driver from the live host's NVML library locations.</summary>
    public ProcessGpuVendorProbe()
        : this(DefaultNvidiaDriverPresent, DefaultProbeTimeout, CreateRealProcess)
    {
    }

    /// <summary>
    ///     Test seam: simulates the NVML driver-presence signal, shortens the per-tool timeout, swaps the process
    ///     factory for a fake that overruns or records a kill, and chooses which platform's adapter-list branch runs.
    /// </summary>
    /// <remarks>
    ///     That makes the no-shelling fast path, the overrun reaping path AND the Windows adapter enumeration all
    ///     exercisable on any host without a real GPU. The platform is a parameter rather than an
    ///     <c>OperatingSystem.IsWindows()</c> call buried in the branch precisely because the Windows branch is the one
    ///     that was wrong and there is no Windows machine to verify it on.
    /// </remarks>
    internal ProcessGpuVendorProbe(Func<bool> nvidiaDriverPresent,
        TimeSpan probeTimeout,
        Func<string, string, IProbeProcess> processFactory,
        ProbePlatform? platform = null)
    {
        _nvidiaDriverPresent = nvidiaDriverPresent ?? throw new ArgumentNullException(nameof(nvidiaDriverPresent));
        _probeTimeout = probeTimeout;
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        _platform = platform ?? CurrentPlatform();
    }

    /// <summary>Which host the adapter-list branch should enumerate for.</summary>
    internal enum ProbePlatform
    {
        Other,
        Windows,
        Linux
    }

    private static ProbePlatform CurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return ProbePlatform.Windows;
        }

        return OperatingSystem.IsLinux() ? ProbePlatform.Linux : ProbePlatform.Other;
    }

    /// <inheritdoc />
    public async Task<DetectedGpuVendor> DetectVendorAsync(CancellationToken ct)
    {
        // Fast path: the NVML runtime library ships with the NVIDIA display driver. If it is present we are confident an
        // NVIDIA adapter is installed — return immediately, with NO process spawned.
        if (_nvidiaDriverPresent())
        {
            return DetectedGpuVendor.Nvidia;
        }

        // A miss is inconclusive: nvml.dll on newer Windows drivers may live only under Program Files\NVIDIA\NVSMI, so confirm
        // with a timeout-bounded nvidia-smi before giving up on NVIDIA.
        if (await NvidiaPresentAsync(ct).ConfigureAwait(false))
        {
            return DetectedGpuVendor.Nvidia;
        }

        var adapterText = await ReadAdapterListAsync(ct).ConfigureAwait(false);
        if (adapterText.Contains("amd", StringComparison.OrdinalIgnoreCase)
            || adapterText.Contains("radeon", StringComparison.OrdinalIgnoreCase)
            || adapterText.Contains("advanced micro devices", StringComparison.OrdinalIgnoreCase))
        {
            return DetectedGpuVendor.Amd;
        }

        if (adapterText.Contains("intel", StringComparison.OrdinalIgnoreCase))
        {
            return DetectedGpuVendor.Intel;
        }

        return DetectedGpuVendor.None;
    }

    // Detects the NVML runtime library (shipped with the NVIDIA display driver, not the CUDA toolkit) at its canonical OS-specific locations: a pure filesystem
    // probe, no process spawned and no I/O beyond File.Exists. Any failure is swallowed to "not present", so detection degrades to the shelling fallback rather than throwing.
    private static bool DefaultNvidiaDriverPresent()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsNvmlPaths().Any(File.Exists);
            }

            if (OperatingSystem.IsLinux())
            {
                return LinuxNvmlPaths().Any(File.Exists);
            }

            return false;
        }
        catch (Exception)
        {
            // A probe of the filesystem must never be fatal; treat any error as "not detected" and fall back to shelling.
            return false;
        }
    }

    private static IEnumerable<string> WindowsNvmlPaths()
    {
        // System32 is where older drivers placed nvml.dll; newer drivers place it under Program Files\NVIDIA\NVSMI
        // (and the legacy "NVIDIA Corporation" vendor folder). Check all so a driver-only install is still detected.
        yield return Path.Combine(Environment.SystemDirectory, "nvml.dll");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            yield return Path.Combine(programFiles, "NVIDIA", "NVSMI", "nvml.dll");
            yield return Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvml.dll");
        }
    }

    private static IEnumerable<string> LinuxNvmlPaths()
    {
        // libnvidia-ml ships as the versioned .so.1 (the unversioned .so is the SDK stub). Check both names across the
        // standard loader directories; the driver installs at least one of these on an NVIDIA host.
        string[] directories =
        [
            "/usr/lib/x86_64-linux-gnu",
            "/usr/lib64",
            "/usr/lib",
            "/usr/lib/aarch64-linux-gnu"
        ];
        string[] names = ["libnvidia-ml.so.1", "libnvidia-ml.so"];

        return directories.SelectMany(dir => names.Select(name => Path.Combine(dir, name)));
    }

    private async Task<bool> NvidiaPresentAsync(CancellationToken ct)
    {
        // nvidia-smi exiting 0 with any GPU name on stdout is sufficient evidence of an NVIDIA adapter.
        var output = await TryRunAsync("nvidia-smi", "--query-gpu=name --format=csv,noheader", ct).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(output);
    }

    private async Task<string> ReadAdapterListAsync(CancellationToken ct)
    {
        return _platform switch
        {
            ProbePlatform.Windows => await ReadWindowsAdapterListAsync(ct).ConfigureAwait(false),
            ProbePlatform.Linux => await TryRunAsync("lspci", string.Empty, ct).ConfigureAwait(false) ?? string.Empty,
            _ => string.Empty
        };
    }

    /// <summary>
    ///     Enumerates Windows display adapters, trying each source in turn until one answers: the CIM query by
    ///     absolute path, then by bare name, then <c>wmic</c> last.
    /// </summary>
    /// <remarks>
    ///     <c>wmic</c> alone is not enough — it is a deprecated Feature-on-Demand absent by default on current Windows
    ///     11, and swallowing that absence made a Vulkan-capable AMD or Intel box run on the CPU silently. NVIDIA never
    ///     reaches here, NVML and <c>nvidia-smi</c> answering first, so nothing on this path changes what an NVIDIA box
    ///     selects. Why each candidate sits where it does, and why the worst case is one timeout rather than one per
    ///     candidate: docs/wiki/03-local-runtime-and-providers.md, "Vendor detection: probe order, the Windows adapter list and the single timeout model".
    /// </remarks>
    private async Task<string> ReadWindowsAdapterListAsync(CancellationToken ct)
    {
        foreach (var (fileName, arguments) in WindowsAdapterListCommands(Environment.SystemDirectory))
        {
            var output = await TryRunAsync(fileName, arguments, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(output))
            {
                return output;
            }
        }

        return string.Empty;
    }

    /// <summary>
    ///     The Windows adapter-list candidates in the order they are tried. Takes the system directory rather than
    ///     reading it, so the preferred absolute path — the part that only exists on Windows — is still assertable from
    ///     a test on any host.
    /// </summary>
    internal static IEnumerable<AdapterListCommand> WindowsAdapterListCommands(string? systemDirectory)
    {
        // -NoProfile so a user profile script cannot slow the probe or change its output, -NonInteractive so nothing can prompt on a headless start. A cmdlet failure
        // against a broken WMI repository leaves stdout empty, which reads as "this source has no answer" and falls through to the next candidate.
        const string CimArguments = "-NoProfile -NonInteractive -Command \"Get-CimInstance -ClassName Win32_VideoController | Select-Object -ExpandProperty Name\"";

        if (!string.IsNullOrEmpty(systemDirectory))
        {
            yield return new AdapterListCommand(Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), CimArguments);
        }

        yield return new AdapterListCommand("powershell", CimArguments);
        yield return new AdapterListCommand("wmic", "path win32_VideoController get name");
    }

    private async Task<string?> TryRunAsync(string fileName, string arguments, CancellationToken ct)
    {
        IProbeProcess? process = null;
        try
        {
            process = _processFactory(fileName, arguments);
            if (!process.Start())
            {
                return null;
            }

            // Bound the read+exit on a timeout linked to the caller's token so a hung tool can't block past either the
            // per-tool ProbeTimeout or the caller's overall budget.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_probeTimeout);
            try
            {
                var stdout = await process.ReadStandardOutputAsync(timeoutCts.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                return process.ExitCode == 0 ? stdout : null;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                // The tool overran ProbeTimeout or the caller's token fired; either way the child is killed and disposed in the finally below, so no orphan survives and
                // "vendor not detected" degrades to the CPU floor rather than freezing provisioning. A cancelled CALLER token, not just ours, is surfaced as cancellation per contract.
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                return null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Tool missing / not on PATH / permission denied — treat as "vendor not detected", never fatal.
            return null;
        }
        finally
        {
            // The single reaping point: on EVERY exit path — success, timeout, caller-cancel or tool error — the whole process tree
            // is killed if still alive, then disposed, so the caller never needs to abandon a live process.
            if (process is not null)
            {
                TryKill(process);
                process.Dispose();
            }
        }
    }

    private static void TryKill(IProbeProcess process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (Exception)
        {
            // Best-effort: the process may have exited between the check and the kill, or be unkillable; either way
            // the probe result (undetected) stands.
        }
    }

    private static IProbeProcess CreateRealProcess(string fileName, string arguments)
    {
        return new RealProbeProcess(fileName, arguments);
    }

    /// <summary>One Windows adapter-list candidate: the executable to run and its command line.</summary>
    internal sealed record AdapterListCommand(string FileName, string Arguments);

    /// <summary>
    ///     Minimal seam over a spawned probe tool process. Production wraps <see cref="Process" />; tests supply a fake
    ///     that can simulate a hang and record that it was killed, so the overrun-reaping path is verifiable without a
    ///     real GPU tool.
    /// </summary>
    internal interface IProbeProcess : IDisposable
    {
        bool HasExited { get; }

        int ExitCode { get; }

        bool Start();

        Task<string> ReadStandardOutputAsync(CancellationToken ct);

        Task WaitForExitAsync(CancellationToken ct);

        void Kill();
    }

    private sealed class RealProbeProcess : IProbeProcess
    {
        private readonly Process _process;

        public RealProbeProcess(string fileName, string arguments)
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
        }

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public bool Start()
        {
            return _process.Start();
        }

        public Task<string> ReadStandardOutputAsync(CancellationToken ct)
        {
            return _process.StandardOutput.ReadToEndAsync(ct);
        }

        public Task WaitForExitAsync(CancellationToken ct)
        {
            return _process.WaitForExitAsync(ct);
        }

        public void Kill()
        {
            _process.Kill(true);
        }

        public void Dispose()
        {
            _process.Dispose();
        }
    }
}
