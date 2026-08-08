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
            vendor = await DetectNonNvidiaVendorAsync(ct).ConfigureAwait(false);
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

    // Non-NVIDIA vendor detection: Linux /sys/class/drm vendor ids, else the Windows adapter-name query. NVIDIA is
    // handled up front by the single nvidia-smi probe, so this branch normally never runs for an NVIDIA box.
    private async Task<GpuVendor> DetectNonNvidiaVendorAsync(CancellationToken ct)
    {
        if (_environment.IsLinux)
        {
            return DetectLinuxDrmVendor();
        }

        return _environment.IsWindows
            ? await ProbeWindowsAdapterVendorAsync(ct).ConfigureAwait(false)
            : GpuVendor.Unknown;
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
    ///     Windows GPU-vendor name, read from the adapter descriptions <c>Win32_VideoController</c> reports.
    ///     <para>
    ///         This used to be a hardcoded <see cref="GpuVendor.Unknown" /> stub, so an AMD or Intel Windows box
    ///         reported <c>gpuVendor: "unknown"</c> on the hardware-profile card while the runtime selector — reading a
    ///         different probe entirely — could be selecting the Vulkan binary for the same machine. The two detectors
    ///         disagreeing is the reason this is implemented rather than left deferred: the profile is what the operator
    ///         is shown.
    ///     </para>
    ///     <para>
    ///         The query goes through <see cref="IProcessProbe" /> under the same wall-clock deadline as
    ///         <c>nvidia-smi</c>, so a wedged WMI repository degrades to <see cref="GpuVendor.Unknown" /> instead of
    ///         stalling the profile — and the OS decision is the injected
    ///         <see cref="IHardwareProbeEnvironment.IsWindows" />, not an inline platform call, so both branches are
    ///         exercisable without a Windows host.
    ///     </para>
    ///     <para>
    ///         <b>This does NOT make the CPU-fallback alert reachable on such a box, and must not be read as if it
    ///         did.</b> That alert needs <c>gpuExpected</c>, which is
    ///         <c>vendor ∈ {nvidia, amd, intel} &amp;&amp; vramBytes &gt; 0</c>, and the VRAM half is still the
    ///         deferred seam below. A vendor without bytes still degrades to CPU mode by the profile's own rule
    ///         (<see cref="HardwareProfile.VramKnown" /> false ⇒ <see cref="HardwareProfile.GpuAccelAvailable" />
    ///         false); what changes is that the profile now names the adapter truthfully instead of saying it does not
    ///         know what it is.
    ///     </para>
    /// </summary>
    private async Task<GpuVendor> ProbeWindowsAdapterVendorAsync(CancellationToken ct)
    {
        foreach (var (fileName, arguments) in WindowsAdapterListCommands())
        {
            var result = await _processProbe.RunAsync(fileName, arguments, ResolveProbeTimeout(), ct).ConfigureAwait(false);
            if (result is null || result.TimedOut || result.ExitCode != 0)
            {
                continue;
            }

            if (MapAdapterVendor(result.StandardOutput) is { } vendor)
            {
                return vendor;
            }
        }

        return GpuVendor.Unknown;
    }

    /// <summary>
    ///     Maps an adapter-description listing to a vendor, or <see langword="null" /> when the listing names no vendor
    ///     this profiler models — which is a different answer from "the tool failed", so the caller can go on to the
    ///     next source rather than accepting an empty read.
    /// </summary>
    internal static GpuVendor? MapAdapterVendor(string adapterNames)
    {
        ArgumentNullException.ThrowIfNull(adapterNames);

        if (adapterNames.Contains("nvidia", StringComparison.OrdinalIgnoreCase))
        {
            // Reachable only when nvidia-smi is absent or unusable while the adapter is genuinely NVIDIA — a
            // driver-present/tool-missing box. Naming it is still better than Unknown.
            return GpuVendor.Nvidia;
        }

        if (adapterNames.Contains("amd", StringComparison.OrdinalIgnoreCase)
            || adapterNames.Contains("radeon", StringComparison.OrdinalIgnoreCase)
            || adapterNames.Contains("advanced micro devices", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVendor.Amd;
        }

        return adapterNames.Contains("intel", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Intel : null;
    }

    /// <summary>
    ///     The Windows adapter-description sources, in the order they are tried.
    ///     <para>
    ///         <c>wmic</c> is LAST and is no longer the only source: it is a deprecated Feature-on-Demand that is not
    ///         installed by default on current Windows 11, and depending on it is what left this detector blind. Windows
    ///         PowerShell 5.1 is in-box on every Windows 11 install and <c>Get-CimInstance</c> is what Microsoft's own
    ///         deprecation notice points at. The absolute System32 path is preferred so a <c>powershell.exe</c> planted
    ///         earlier on <c>PATH</c> cannot answer for it.
    ///     </para>
    /// </summary>
    private static IEnumerable<(string FileName, IReadOnlyList<string> Arguments)> WindowsAdapterListCommands()
    {
        string[] cimArguments =
        [
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "Get-CimInstance -ClassName Win32_VideoController | Select-Object -ExpandProperty Name"
        ];

        var systemDirectory = Environment.SystemDirectory;
        if (!string.IsNullOrEmpty(systemDirectory))
        {
            yield return (Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), cimArguments);
        }

        yield return ("powershell", cimArguments);
        yield return ("wmic", ["path", "win32_VideoController", "get", "name"]);
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
