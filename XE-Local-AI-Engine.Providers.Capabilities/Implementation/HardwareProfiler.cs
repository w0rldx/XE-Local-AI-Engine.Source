namespace XE_Local_AI_Engine.Providers.Capabilities.Implementation;

using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Capabilities.Contracts;
using XE_Local_AI_Engine.Providers.Capabilities.Options;

/// <summary>
///     Cross-platform <see cref="IHardwareProfiler" /> that reports RAM, VRAM bytes, GPU vendor, CPU, and free disk
///     on Linux and Windows. The provider remains independent of any runtime implementation.
/// </summary>
/// <remarks>
///     RAM comes from Linux <c>/proc/meminfo</c> or a Windows OS query; VRAM from a SINGLE <c>nvidia-smi</c>
///     invocation, with Windows adapter names or Linux <c>/sys/class/drm</c> naming a non-NVIDIA vendor whose VRAM
///     stays unknown. <b>Every probe is bounded</b> by
///     <see cref="HardwareProfilerOptions.HardwareProbeTimeoutSeconds" />, degrading to the cached profile or the
///     CPU-safe default (a hung <c>nvidia-smi</c> once stalled provisioning); unknown VRAM means no GPU acceleration.
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
            // No NVIDIA GPU (or the probe timed out with no cache): fall back to OS vendor detection. Non-NVIDIA VRAM
            // stays unknown on Linux and Windows, which keeps the profile in CPU mode.
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

    private RamSnapshot ProbeRam()
    {
        if (_environment.IsLinux)
        {
            var memInfo = _environment.ReadProcMemInfo();
            if (memInfo is not null
                && TryParseMemInfoKilobytes(memInfo, "MemTotal", out var totalKb)
                && TryParseMemInfoKilobytes(memInfo, "MemAvailable", out var availableKb))
            {
                return new RamSnapshot(totalKb * 1024L, availableKb * 1024L);
            }
        }

        // Windows (and Linux fallback when meminfo is unavailable): OS query.
        return new RamSnapshot(_environment.GetTotalPhysicalMemoryBytes(), _environment.GetAvailableMemoryBytes());
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
            // Windows vendor detection has already run, but AMD/Intel VRAM is unavailable. Returning null keeps the
            // profile CPU-safe; NVIDIA VRAM is handled earlier by the shared nvidia-smi probe.
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

    // Parses the consolidated nvidia-smi output: one comma-separated row per GPU carrying name, total and free VRAM in
    // MiB, first usable GPU wins. A row with no comma is a banner, not a device; an unparseable total means VRAM unknown.
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
    ///     Windows AMD and Intel VRAM probing is not implemented, because no vendor-neutral probe has been validated on
    ///     Windows; it returns the fail-safe <see langword="null" />, meaning VRAM unknown and so CPU mode.
    /// </summary>
    /// <remarks>NVIDIA on Windows is unaffected, because the shared <c>nvidia-smi</c> branch handles it.</remarks>
    private static Task<long?> ProbeWindowsNonNvidiaVramAsync(GpuVendor vendor, CancellationToken ct)
    {
        _ = vendor;
        _ = ct;

        // System.Management is intentionally not referenced solely for this unsupported probe, keeping the project
        // cross-platform. Unknown VRAM must remain a CPU-mode result rather than an optimistic GPU-memory estimate.
        return Task.FromResult<long?>(null);
    }

    /// <summary>
    ///     Windows GPU-vendor name, read from the adapter descriptions <c>Win32_VideoController</c> reports.
    /// </summary>
    /// <remarks>
    ///     Implemented, not deferred: the profile is what the operator is shown, and a stub vendor contradicted the
    ///     runtime selector's own probe. It runs through <see cref="IProcessProbe" /> under the <c>nvidia-smi</c>
    ///     deadline, and the OS test is the injected <see cref="IHardwareProbeEnvironment.IsWindows" />. It does NOT
    ///     make the CPU-fallback alert reachable: that needs <c>gpuExpected</c>, a known vendor AND
    ///     <c>vramBytes &gt; 0</c>, whose VRAM half is still the deferred seam below.
    /// </remarks>
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

    /// <summary>The Windows adapter-description sources, in the order they are tried.</summary>
    /// <remarks>
    ///     <c>wmic</c> is LAST and not the only source: it is a deprecated Feature-on-Demand not installed by default
    ///     on current Windows 11, and depending on it is what left this detector blind. Windows PowerShell 5.1 is
    ///     in-box on every Windows 11 install and <c>Get-CimInstance</c> is what Microsoft's own deprecation notice
    ///     points at. The absolute System32 path is preferred, so a <c>powershell.exe</c> planted earlier on
    ///     <c>PATH</c> cannot answer for it.
    /// </remarks>
    private static IEnumerable<AdapterListCommand> WindowsAdapterListCommands()
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
            yield return new AdapterListCommand(Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), cimArguments);
        }

        yield return new AdapterListCommand("powershell", cimArguments);
        yield return new AdapterListCommand("wmic", ["path", "win32_VideoController", "get", "name"]);
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

    // The host's physical memory in bytes as the probe read it: the installed total and the currently available slice.
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct RamSnapshot(long TotalRamBytes, long AvailableRamBytes);

    // One Windows adapter-description source: the executable to run and its argument vector.
    private sealed record AdapterListCommand(string FileName, IReadOnlyList<string> Arguments);

    // The single consolidated nvidia-smi read, projected: NVIDIA presence, total and free VRAM in bytes (first GPU
    // wins, null when unparseable), and whether the probe overran its deadline and was killed.
    private readonly record struct NvidiaProbe(bool Present, long? TotalVramBytes, long? FreeVramBytes, bool TimedOut)
    {
        public static NvidiaProbe Absent { get; } = new(Present: false, TotalVramBytes: null, FreeVramBytes: null, TimedOut: false);

        public static NvidiaProbe Timeout { get; } = new(Present: false, TotalVramBytes: null, FreeVramBytes: null, TimedOut: true);
    }
}
