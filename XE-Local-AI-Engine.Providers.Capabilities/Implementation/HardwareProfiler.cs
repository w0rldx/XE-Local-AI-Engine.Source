namespace XE_Local_AI_Engine.Providers.Capabilities.Implementation;

using System.Globalization;
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
///             VRAM — <c>nvidia-smi --query-gpu=memory.total</c> (NVIDIA, shared across both OSes) → else Windows
///             DXGI/WMI seam (operator-filled on Win11, see <see cref="ProbeWindowsNonNvidiaVramAsync" />) / Linux
///             <c>/sys/class/drm</c> vendor query → else <see cref="HardwareProfile.VramKnown" /> <see langword="false" />.
///         </item>
///     </list>
///     Degrade rule: <see cref="HardwareProfile.VramKnown" /> <see langword="false" /> ⇒
///     <see cref="HardwareProfile.GpuAccelAvailable" /> <see langword="false" />. The profile is cached in memory and
///     re-probed only on <c>forceRefresh:true</c>; registered as a singleton.
/// </remarks>
internal sealed class HardwareProfiler : IHardwareProfiler
{
    // PCI vendor ids from /sys/class/drm/*/device/vendor (4-digit hex, no 0x prefix, upper-cased by the environment).
    private const string PciVendorNvidia = "10DE";
    private const string PciVendorAmd = "1002";
    private const string PciVendorIntel = "8086";
    private readonly IHardwareProbeEnvironment _environment;
    private readonly HardwareProfilerOptions _options;

    private readonly IProcessProbe _processProbe;

    private volatile HardwareProfile? _cachedProfile;

    public HardwareProfiler(IProcessProbe processProbe,
        IHardwareProbeEnvironment environment,
        HardwareProfilerOptions options)
    {
        _processProbe = processProbe ?? throw new ArgumentNullException(nameof(processProbe));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
        var vendor = await DetectGpuVendorAsync(ct).ConfigureAwait(false);
        var vramBytes = await DetectVramAsync(vendor, ct).ConfigureAwait(false);
        var availableVramBytes = await DetectAvailableVramAsync(vendor, vramBytes, ct).ConfigureAwait(false);

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

    private async Task<GpuVendor> DetectGpuVendorAsync(CancellationToken ct)
    {
        // NVIDIA is unambiguous when nvidia-smi yields a GPU name; check it first on every OS.
        if (await NvidiaPresentAsync(ct).ConfigureAwait(false))
        {
            return GpuVendor.Nvidia;
        }

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

    private async Task<long?> DetectVramAsync(GpuVendor vendor, CancellationToken ct)
    {
        // Shared across Linux + Windows: nvidia-smi reports exact total VRAM in MiB.
        if (vendor == GpuVendor.Nvidia)
        {
            var nvidiaVram = await ProbeNvidiaVramBytesAsync(ct).ConfigureAwait(false);
            if (nvidiaVram is not null)
            {
                return nvidiaVram;
            }
        }

        if (_environment.IsWindows)
        {
            // DXGI DedicatedVideoMemory (vendor-neutral) → WMI vendor-name only. Operator-filled seam on Win11.
            return await ProbeWindowsNonNvidiaVramAsync(vendor, ct).ConfigureAwait(false);
        }

        // Linux non-NVIDIA: no reliable byte-accurate VRAM source without extra deps → degrade to unknown.
        return null;
    }

    // Free VRAM is only byte-accurate on NVIDIA (nvidia-smi memory.free). For every other vendor — and when total VRAM
    // itself is unknown — the free baseline is null, which forces the capacity gate onto its total-VRAM fallback. This
    // is the GPU analogue of AvailableRamBytes: it nets out VRAM already resident in loaded llama-server processes.
    private async Task<long?> DetectAvailableVramAsync(GpuVendor vendor, long? totalVramBytes, CancellationToken ct)
    {
        if (vendor != GpuVendor.Nvidia || totalVramBytes is null)
        {
            return null;
        }

        return await ProbeNvidiaMemoryMibBytesAsync("memory.free", ct).ConfigureAwait(false);
    }

    private async Task<bool> NvidiaPresentAsync(CancellationToken ct)
    {
        var result = await _processProbe
                           .RunAsync("nvidia-smi", ["--query-gpu=name", "--format=csv,noheader"], ct)
                           .ConfigureAwait(false);
        return result is { ExitCode: 0 } && !string.IsNullOrWhiteSpace(result.StandardOutput);
    }

    private Task<long?> ProbeNvidiaVramBytesAsync(CancellationToken ct)
    {
        return ProbeNvidiaMemoryMibBytesAsync("memory.total", ct);
    }

    // Runs a single-field nvidia-smi memory query and returns the first GPU's figure in bytes (the field is reported in
    // MiB). Shared by the total- and free-VRAM probes so both parse identically.
    private async Task<long?> ProbeNvidiaMemoryMibBytesAsync(string queryField, CancellationToken ct)
    {
        var result = await _processProbe
                           .RunAsync("nvidia-smi", [$"--query-gpu={queryField}", "--format=csv,noheader,nounits"], ct)
                           .ConfigureAwait(false);

        if (result is not { ExitCode: 0 })
        {
            return null;
        }

        // First PARSEABLE line is the first GPU's VRAM figure in MiB. Scan past any leading non-numeric lines
        // (e.g. an nvidia-smi warning banner) so a noise line doesn't defeat detection; "first GPU wins" still holds.
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mib) && mib > 0)
            {
                return mib * 1024L * 1024L;
            }
        }

        return null;
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
}
