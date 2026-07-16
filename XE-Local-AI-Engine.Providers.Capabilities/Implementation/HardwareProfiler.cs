namespace XE_Local_AI_Engine.Providers.Capabilities.Implementation;

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Capabilities.Contracts;
using XE_Local_AI_Engine.Providers.Capabilities.Options;

/// <summary>
///     Cross-platform <see cref="IHardwareProfiler" />. Extracted from (and supersedes) the Linux-shell-only
///     <c>HostAgent.Linux.CapabilityDetector</c> — which measured no RAM/VRAM bytes — and adds RAM + VRAM-bytes +
///     GPU-vendor detection on both Linux and Windows. Has ZERO <c>HostAgent.*</c> dependency, so the capabilities
///     provider stays decoupled from the host-agent runtime.
/// </summary>
/// <remarks>
///     Probe order:
///     <list type="bullet">
///         <item>RAM — Linux <c>/proc/meminfo</c> (MemTotal/MemAvailable); Windows OS query.</item>
///         <item>
///             VRAM — a SINGLE <c>nvidia-smi --query-gpu=name,memory.total,memory.free</c> invocation (NVIDIA, shared
///             across both OSes) → else Windows DXGI/WMI seam (operator-filled on Win11, see
///             <see cref="ProbeWindowsNonNvidiaVramAsync" />) / Linux <c>/sys/class/drm</c> vendor query → else
///             <see cref="HardwareProfile.VramKnown" /> <see langword="false" />.
///         </item>
///     </list>
///     <para>
///         <b>Every process probe is bounded.</b> The <c>nvidia-smi</c> call runs under a wall-clock deadline
///         (<see cref="HardwareProfilerOptions.HardwareProbeTimeoutSeconds" />); on overrun the process tree is killed and
///         the profiler degrades to the most recent cached profile — or, when none exists, the CPU-safe default (VRAM
///         unknown ⇒ CPU mode). This closes the already-paid-for trap where a hung <c>nvidia-smi</c> stalled first-run
///         provisioning (and, via the capacity gate, an admission decision) indefinitely.
///     </para>
///     Degrade rule: <see cref="HardwareProfile.VramKnown" /> <see langword="false" /> ⇒
///     <see cref="HardwareProfile.GpuAccelAvailable" /> <see langword="false" />. The profile is cached in memory and
///     re-probed only on <c>forceRefresh:true</c>; registered as a singleton.
/// </remarks>
internal sealed class HardwareProfiler : IHardwareProfiler
{
    private const string NvidiaSmi = "nvidia-smi";

    // PCI vendor ids from /sys/class/drm/*/device/vendor (4-digit hex, no 0x prefix, upper-cased by the environment).
    private const string PciVendorNvidia = "10DE";
    private const string PciVendorAmd = "1002";
    private const string PciVendorIntel = "8086";
    private readonly IHardwareProbeEnvironment _environment;
    private readonly ILogger<HardwareProfiler> _logger;
    private readonly IHardwareProbeMetrics _metrics;
    private readonly HardwareProfilerOptions _options;

    private readonly IProcessProbe _processProbe;

    private volatile HardwareProfile? _cachedProfile;

    public HardwareProfiler(IProcessProbe processProbe,
        IHardwareProbeEnvironment environment,
        HardwareProfilerOptions options,
        ILogger<HardwareProfiler>? logger = null,
        IHardwareProbeMetrics? metrics = null)
    {
        _processProbe = processProbe ?? throw new ArgumentNullException(nameof(processProbe));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<HardwareProfiler>.Instance;
        _metrics = metrics ?? NullHardwareProbeMetrics.Instance;
    }

    /// <inheritdoc />
    public async Task<HardwareProfile> GetProfileAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && _cachedProfile is { } cached)
        {
            return cached;
        }

        // The probe is read-only and idempotent, so an un-serialized concurrent first-probe is harmless (last write
        // wins on the cached field). Avoiding a lock keeps the singleton non-disposable.
        var profile = await ProbeAsync(ct).ConfigureAwait(false);
        _cachedProfile = profile;
        return profile;
    }

    private async Task<HardwareProfile> ProbeAsync(CancellationToken ct)
    {
        var (totalRam, availableRam) = ProbeRam();

        // ONE bounded nvidia-smi call yields name + total + free for every GPU, replacing the former three sequential
        // unbounded invocations.
        var nvidia = await ProbeNvidiaAsync(ct).ConfigureAwait(false);

        if (nvidia.TimedOut)
        {
            _metrics.RecordProbeTimeout(NvidiaSmi);

            // The GPU probe was killed for overrunning its deadline. Prefer the last good profile (it keeps the real
            // VRAM figures) over a freshly-degraded one; with no cache yet, fall through to the CPU-safe degrade below.
            if (_cachedProfile is { } lastGood)
            {
                _logger.LogWarning("nvidia-smi hardware probe timed out; reusing the last cached hardware profile.");
                return lastGood;
            }

            _logger.LogWarning("nvidia-smi hardware probe timed out and no cached profile exists; degrading to the CPU-safe default (VRAM unknown).");
        }

        GpuVendor vendor;
        long? vramBytes;
        long? availableVramBytes;

        if (nvidia.Present)
        {
            vendor = GpuVendor.Nvidia;
            vramBytes = nvidia.TotalVramBytes;
            // Free VRAM is only meaningful when the total was parsed from the same row (else the fallback total-VRAM math
            // in the capacity gate applies). null total ⇒ VRAM unknown ⇒ CPU mode.
            availableVramBytes = vramBytes is not null ? nvidia.FreeVramBytes : null;
        }
        else
        {
            // No NVIDIA GPU (or the probe timed out with no cache): fall back to the OS vendor seam. VRAM stays unknown
            // on Linux non-NVIDIA and on the not-yet-implemented Windows DXGI seam ⇒ CPU mode.
            vendor = DetectNonNvidiaVendor();
            vramBytes = await DetectNonNvidiaVramAsync(vendor, ct).ConfigureAwait(false);
            availableVramBytes = null;
        }

        var vramKnown = vramBytes is not null;
        // Degrade rule: VRAM unknown ⇒ no GPU budget, regardless of a detected vendor.
        var gpuAccelAvailable = vramKnown && vendor is GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel;

        return new HardwareProfile
        {
            TotalRamBytes = totalRam,
            AvailableRamBytes = availableRam,
            VramBytes = vramBytes,
            AvailableVramBytes = availableVramBytes,
            VramKnown = vramKnown,
            GpuVendor = vendor,
            GpuAccelAvailable = gpuAccelAvailable,
            CpuCores = _environment.ProcessorCount,
            FreeDiskBytes = _environment.GetFreeDiskBytes(_options.ModelsVolumePath)
        };
    }

    private (long TotalRamBytes, long AvailableRamBytes) ProbeRam()
    {
        if (_environment.IsLinux)
        {
            var memInfo = _environment.ReadProcMemInfo();
            if (memInfo is not null
                && TryParseMemInfoKilobytes(memInfo, "MemTotal", out var totalKb)
                && TryParseMemInfoKilobytes(memInfo, "MemAvailable", out var availableKb))
            {
                return (totalKb * 1024L, availableKb * 1024L);
            }
        }

        // Windows (and Linux fallback when meminfo is unavailable): OS query.
        return (_environment.GetTotalPhysicalMemoryBytes(), _environment.GetAvailableMemoryBytes());
    }

    // Non-NVIDIA vendor detection: Linux /sys/class/drm vendor ids, else the Windows adapter-name seam. NVIDIA is handled
    // up front by the single nvidia-smi probe, so this branch never runs for an NVIDIA box.
    private GpuVendor DetectNonNvidiaVendor()
    {
        if (_environment.IsLinux)
        {
            return DetectLinuxDrmVendor();
        }

        if (_environment.IsWindows)
        {
            // DXGI/WMI vendor-name seam (operator-filled on Win11). Returns Unknown on this Linux box.
            return ProbeWindowsAdapterVendor();
        }

        return GpuVendor.Unknown;
    }

    private GpuVendor DetectLinuxDrmVendor()
    {
        var vendorIds = _environment.ReadDrmVendorIds();
        if (vendorIds.Contains(PciVendorNvidia, StringComparer.OrdinalIgnoreCase))
        {
            return GpuVendor.Nvidia;
        }

        if (vendorIds.Contains(PciVendorAmd, StringComparer.OrdinalIgnoreCase))
        {
            return GpuVendor.Amd;
        }

        if (vendorIds.Contains(PciVendorIntel, StringComparer.OrdinalIgnoreCase))
        {
            return GpuVendor.Intel;
        }

        return vendorIds.Count > 0 ? GpuVendor.Unknown : GpuVendor.None;
    }

    private Task<long?> DetectNonNvidiaVramAsync(GpuVendor vendor, CancellationToken ct)
    {
        if (_environment.IsWindows)
        {
            // DXGI DedicatedVideoMemory (vendor-neutral) → WMI vendor-name only. Operator-filled seam on Win11.
            return ProbeWindowsNonNvidiaVramAsync(vendor, ct);
        }

        // Linux non-NVIDIA: no reliable byte-accurate VRAM source without extra deps → degrade to unknown.
        return Task.FromResult<long?>(null);
    }

    // Runs the SINGLE consolidated nvidia-smi query (name + total + free) under the configured wall-clock deadline and
    // projects it to an NvidiaProbe. A missing tool / non-zero exit ⇒ Absent; an overrun ⇒ Timeout (the caller degrades).
    private async Task<NvidiaProbe> ProbeNvidiaAsync(CancellationToken ct)
    {
        var result = await _processProbe
                           .RunAsync(NvidiaSmi,
                               ["--query-gpu=name,memory.total,memory.free", "--format=csv,noheader,nounits"],
                               ResolveProbeTimeout(),
                               ct)
                           .ConfigureAwait(false);

        if (result is null)
        {
            return NvidiaProbe.Absent; // tool missing / not on PATH / spawn failure — treat as "no NVIDIA GPU".
        }

        if (result.TimedOut)
        {
            return NvidiaProbe.Timeout;
        }

        if (result.ExitCode != 0)
        {
            return NvidiaProbe.Absent; // nvidia-smi present but reported no manageable devices.
        }

        return ParseNvidiaCsv(result.StandardOutput);
    }

    private TimeSpan ResolveProbeTimeout()
    {
        return TimeSpan.FromSeconds(_options.HardwareProbeTimeoutSeconds);
    }

    // Parses the consolidated nvidia-smi output: one comma-separated device row per GPU carrying the name then the total
    // and free VRAM in MiB. Multi-GPU yields several rows and the first usable GPU wins (matching the prior per-field
    // probes, which each took the first parseable figure). A leading warning banner is skipped — a row with no comma is
    // not a device row — and a named row whose total does not parse still marks an NVIDIA GPU present with VRAM unknown.
    private static NvidiaProbe ParseNvidiaCsv(string stdout)
    {
        var present = false;
        long? totalBytes = null;
        long? freeBytes = null;

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var columns = line.Split(',');
            if (columns.Length < 2)
            {
                // Not a data row (e.g. an "nvidia-smi WARNING: ..." banner with no CSV columns).
                continue;
            }

            var name = columns[0].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            // A named row means an NVIDIA GPU is present (vendor signal). Take the VRAM figures from the FIRST row that
            // carries a parseable total, reading free from that same row so total/free describe one device, then stop.
            present = true;
            if (TryParseMib(columns[1], out var total))
            {
                totalBytes = total;
                freeBytes = columns.Length >= 3 && TryParseMib(columns[2], out var free) ? free : null;
                break;
            }
        }

        return present
            ? new NvidiaProbe(Present: true, totalBytes, freeBytes, TimedOut: false)
            : NvidiaProbe.Absent;
    }

    private static bool TryParseMib(string token, out long bytes)
    {
        if (long.TryParse(token.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mib) && mib > 0)
        {
            bytes = mib * 1024L * 1024L;
            return true;
        }

        bytes = 0;
        return false;
    }

    /// <summary>
    ///     Windows non-NVIDIA VRAM-bytes seam. Not yet implemented: the eventual implementation would read DXGI
    ///     <c>IDXGIAdapter::GetDesc().DedicatedVideoMemory</c> (vendor-neutral, accurate &gt;4GB) then WMI
    ///     <c>Win32_VideoController</c> vendor-name only. This needs validating on a real Win11 box; until then it
    ///     degrades to <see langword="null" /> (VRAM unknown ⇒ CPU mode). NVIDIA on Windows is already covered by the
    ///     shared <c>nvidia-smi</c> branch above, so this gap only affects AMD/Intel on Windows.
    /// </summary>
    private static Task<long?> ProbeWindowsNonNvidiaVramAsync(GpuVendor vendor, CancellationToken ct)
    {
        _ = vendor;
        _ = ct;

        // Known, intentionally-deferred limitation (release cleanup, doc-and-defer): AMD/Intel GPUs on Windows have no
        // VRAM probe and fall back to CPU mode; NVIDIA on Windows is unaffected (nvidia-smi branch above). The DXGI
        // P/Invoke can't be live-verified on this Linux/WSL box, so it is deferred to a Windows-capable session.
        // Win11 follow-up: implement DXGI DedicatedVideoMemory P/Invoke (vendor-neutral, no NuGet) → WMI
        // Win32_VideoController vendor-name fallback. System.Management is intentionally NOT referenced (avoids a
        // Windows-only NuGet on this cross-platform project); prefer DXGI P/Invoke validated on a real Win11 box.
        // Until then this degrades to null (VRAM unknown ⇒ CPU mode) — the always-correct floor.
        return Task.FromResult<long?>(null);
    }

    /// <summary>
    ///     Windows GPU-vendor-name seam from the adapter description (DXGI/WMI). Not yet implemented: returns
    ///     <see cref="GpuVendor.Unknown" /> so the NVIDIA-via-nvidia-smi path still works and non-NVIDIA Windows boxes
    ///     degrade to CPU mode until this is implemented and validated on Win11.
    /// </summary>
    private static GpuVendor ProbeWindowsAdapterVendor()
    {
        // Known, intentionally-deferred limitation (release cleanup, doc-and-defer): the Windows non-NVIDIA vendor
        // probe is not built this release because it can't be live-verified on this Linux/WSL box; AMD/Intel on Windows
        // therefore report Unknown and run in CPU mode, while NVIDIA on Windows is unaffected (nvidia-smi).
        // Win11 follow-up: enumerate DXGI adapter descriptions / WMI Win32_VideoController.Name and map
        // "AMD"/"Radeon"/"Advanced Micro Devices"→Amd, "Intel"→Intel.
        return GpuVendor.Unknown;
    }

    private static bool TryParseMemInfoKilobytes(string memInfo, string key, out long kilobytes)
    {
        kilobytes = 0;
        foreach (var line in memInfo.Split('\n'))
        {
            if (!line.StartsWith(key, StringComparison.Ordinal))
            {
                continue;
            }

            // Match the exact key followed by ':' (so "MemTotal" does not match "MemTotalSomethingElse").
            var afterKey = line[key.Length..].TrimStart();
            if (!afterKey.StartsWith(':'))
            {
                continue;
            }

            var valuePart = afterKey[1..].Trim();
            // Format: "<number> kB". Take the leading numeric token.
            var spaceIndex = valuePart.IndexOf(value: ' ', StringComparison.Ordinal);
            var numberToken = spaceIndex >= 0 ? valuePart[..spaceIndex] : valuePart;
            if (long.TryParse(numberToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out kilobytes))
            {
                return true;
            }
        }

        return false;
    }

    // The single consolidated nvidia-smi read, projected: whether an NVIDIA GPU is present (vendor signal), its total /
    // free VRAM in bytes (first GPU wins; null when unparseable), and whether the probe was killed for overrunning its
    // deadline (the caller then degrades to the cached/CPU-safe profile).
    private readonly record struct NvidiaProbe(bool Present, long? TotalVramBytes, long? FreeVramBytes, bool TimedOut)
    {
        public static NvidiaProbe Absent { get; } = new(Present: false, TotalVramBytes: null, FreeVramBytes: null, TimedOut: false);

        public static NvidiaProbe Timeout { get; } = new(Present: false, TotalVramBytes: null, FreeVramBytes: null, TimedOut: true);
    }
}
