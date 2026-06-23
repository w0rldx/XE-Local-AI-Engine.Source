namespace XE_Local_AI_Engine.Providers.LlamaServer;

using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
///     Default GPU-vendor probe. Prefers a non-shelling driver-presence signal (the NVML runtime library that ships
///     with the NVIDIA display driver), and only shells out to lightweight, ubiquitous tools when that fast signal is
///     absent. Detection failure degrades to <see cref="DetectedGpuVendor.None" /> (CPU floor) — never throws.
/// </summary>
/// <remarks>
///     <para>
///         <b>NVIDIA fast path (no shelling).</b> The NVML runtime library — <c>nvml.dll</c> on Windows,
///         <c>libnvidia-ml.so</c>/<c>libnvidia-ml.so.1</c> on Linux — ships with the NVIDIA <em>display driver</em>
///         (not the CUDA toolkit), so its presence is a valid "an NVIDIA driver is installed" signal. When present we
///         report <see cref="DetectedGpuVendor.Nvidia" /> immediately, never spawning a process. Because newer Windows
///         drivers may place <c>nvml.dll</c> only under <c>Program Files\NVIDIA\NVSMI</c> (not <c>System32</c>), its
///         <em>absence</em> is NOT proof of "no NVIDIA" — so we still fall through to <c>nvidia-smi</c> as confirmation.
///     </para>
///     <para>
///         <b>Process reaping &amp; the single timeout model.</b> Every child process the probe spawns is owned by a
///         <c>using</c> block and is always killed (entire tree) + disposed before <see cref="TryRunAsync" /> returns —
///         on the happy path, on the per-tool <see cref="DefaultProbeTimeout" /> overrun, and on caller cancellation. The probe
///         therefore never leaves a live child behind for the caller to abandon. The only timeout the probe enforces is
///         the per-tool <see cref="DefaultProbeTimeout" />; the caller bounds the <em>whole</em> probe by passing a cancellation
///         token (e.g. <c>CancellationTokenSource.CancelAfter</c>), and cancellation flows into the same reaping path.
///         There is no second, redundant wall-clock race — see <c>FirstRunModelProvisioningService</c>.
///     </para>
///     <para>
///         Probe order: NVML driver-presence (NVIDIA, no shelling) → <c>nvidia-smi</c> (NVIDIA confirmation/fallback) →
///         a platform adapter list (<c>wmic</c>/<c>lspci</c>) for AMD/Intel.
///     </para>
/// </remarks>
public sealed class ProcessGpuVendorProbe : IGpuVendorProbe
{
    // Hard cap per probe tool. Without it a hung tool blocks until the caller's token fires: nvidia-smi can stall
    // indefinitely under some Windows driver/WMI states, and the deprecated wmic can be very slow or absent. A hung GPU
    // probe would otherwise freeze first-run model provisioning. On timeout we kill the tool (entire tree) and treat the
    // vendor as undetected — degrading to the CPU runtime, which always works.
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(8);

    private readonly Func<bool> _nvidiaDriverPresent;
    private readonly TimeSpan _probeTimeout;
    private readonly Func<string, string, IProbeProcess> _processFactory;

    /// <summary>Creates a probe that detects the NVIDIA driver from the live host's NVML library locations.</summary>
    public ProcessGpuVendorProbe()
        : this(DefaultNvidiaDriverPresent, DefaultProbeTimeout, CreateRealProcess)
    {
    }

    /// <summary>
    ///     Test seam: lets a unit test simulate the NVML driver-presence signal, shorten the per-tool timeout, and swap
    ///     the process factory for a fake that overruns/records-kill — so the no-shelling fast path AND the overrun
    ///     reaping path are exercisable on any host without a real GPU.
    /// </summary>
    internal ProcessGpuVendorProbe(Func<bool> nvidiaDriverPresent, TimeSpan probeTimeout, Func<string, string, IProbeProcess> processFactory)
    {
        _nvidiaDriverPresent = nvidiaDriverPresent ?? throw new ArgumentNullException(nameof(nvidiaDriverPresent));
        _probeTimeout = probeTimeout;
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
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

        // NVML was not found at a known location (or this is a non-NVIDIA / driver-only-elsewhere host). nvml.dll on
        // newer Windows drivers may live only under Program Files\NVIDIA\NVSMI, so a miss is inconclusive — confirm with
        // nvidia-smi (timeout-bounded) before giving up on NVIDIA.
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

    // Detects the NVML runtime library (shipped with the NVIDIA display driver, not the CUDA toolkit) at its canonical
    // OS-specific locations. Pure filesystem probe — no process spawned, no I/O beyond File.Exists. Any failure is
    // swallowed to a "not present" so detection degrades to the shelling fallback rather than throwing.
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
        if (OperatingSystem.IsWindows())
        {
            return await TryRunAsync("wmic", "path win32_VideoController get name", ct).ConfigureAwait(false) ?? string.Empty;
        }

        if (OperatingSystem.IsLinux())
        {
            return await TryRunAsync("lspci", string.Empty, ct).ConfigureAwait(false) ?? string.Empty;
        }

        return string.Empty;
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
                // Either the tool overran ProbeTimeout (e.g. nvidia-smi hanging) or the caller's token fired. In BOTH
                // cases the child is killed+disposed in the finally below, so no orphan survives; report "vendor not
                // detected" so detection degrades to the CPU floor instead of freezing provisioning. When the caller's
                // own token (not just our timeout) was cancelled, surface that as cancellation to honor the contract.
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
            // Guarantee no orphaned child on EVERY exit path (success, timeout, caller-cancel, or tool error): kill the
            // whole process tree if it is still alive, then dispose. This is the single reaping point — the caller never
            // needs to abandon a live process.
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
