namespace XE_Local_AI_Engine.Tests.Providers.Capabilities;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Capabilities.Contracts;
using XE_Local_AI_Engine.Providers.Capabilities.Implementation;
using XE_Local_AI_Engine.Providers.Capabilities.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="HardwareProfiler" /> tests: Windows and Linux RAM/VRAM/vendor detection, the degrade-to-CPU path when
///     VRAM cannot be probed, and the gate proving the profiler project carries no HostAgent dependency. The process +
///     environment probe seams are faked so detection is exercised with canned output and NO real GPU, process spawn or
///     platform pin.
/// </summary>
public sealed class HardwareProfilerTests
{
    private const long Mib = 1024L * 1024L;
    private const long Kb = 1024L;

    [Test]
    public async Task HardwareProfiler_Linux_ParsesMemInfoAndNvidiaSmi()
    {
        const string memInfo = "MemTotal:       32830012 kB\nMemFree:         1000000 kB\nMemAvailable:   24000000 kB\n";
        var probe = new FakeProcessProbe()
                    .OnNvidiaName("NVIDIA GeForce RTX 4090\n")
                    .OnNvidiaMemoryTotal("24564\n");
        var environment = new FakeEnvironment
        {
            IsLinux = true,
            ProcMemInfo = memInfo,
            ProcessorCount = 16,
            FreeDiskBytes = 500L * 1024 * 1024 * 1024
        };

        var profiler = new HardwareProfiler(probe, environment, new HardwareProfilerOptions());
        var profile = await profiler.GetProfileAsync(forceRefresh: false, CancellationToken.None);

        AssertEx.Equal(32830012L * Kb, profile.TotalRamBytes);
        AssertEx.Equal(24000000L * Kb, profile.AvailableRamBytes);
        AssertEx.Equal(GpuVendor.Nvidia, profile.GpuVendor);
        AssertEx.True(profile.VramKnown);
        AssertEx.Equal(24564L * Mib, profile.VramBytes!.Value);
        AssertEx.True(profile.GpuAccelAvailable);
        AssertEx.Equal(expected: 16, profile.CpuCores);
        AssertEx.Equal(500L * 1024 * 1024 * 1024, profile.FreeDiskBytes);
    }

    [Test]
    public async Task HardwareProfiler_Nvidia_ParsesFreeVram_AsAvailableVramBytes()
    {
        // nvidia-smi memory.free yields the free-VRAM baseline the capacity gate uses; it is reported in MiB like total.
        const string memInfo = "MemTotal:       32830012 kB\nMemAvailable:   24000000 kB\n";
        var probe = new FakeProcessProbe()
                    .OnNvidiaName("NVIDIA GeForce RTX 4090\n")
                    .OnNvidiaMemoryTotal("24564\n")
                    .OnNvidiaMemoryFree("8192\n");
        var environment = new FakeEnvironment
        {
            IsLinux = true,
            ProcMemInfo = memInfo,
            ProcessorCount = 16
        };

        var profiler = new HardwareProfiler(probe, environment, new HardwareProfilerOptions());
        var profile = await profiler.GetProfileAsync(forceRefresh: false, CancellationToken.None);

        AssertEx.Equal(24564L * Mib, profile.VramBytes!.Value);
        AssertEx.Equal(8192L * Mib, profile.AvailableVramBytes!.Value);
    }

    [Test]
    public async Task HardwareProfiler_Nvidia_WhenFreeVramUnprobed_LeavesAvailableVramNull()
    {
        // When nvidia-smi reports total but the free query fails, AvailableVramBytes stays null so the capacity gate
        // falls back to total-VRAM math rather than trusting a missing free figure.
        const string memInfo = "MemTotal:       32830012 kB\nMemAvailable:   24000000 kB\n";
        var probe = new FakeProcessProbe()
                    .OnNvidiaName("NVIDIA GeForce RTX 4090\n")
                    .OnNvidiaMemoryTotal("24564\n"); // no OnNvidiaMemoryFree → free query returns null.
        var environment = new FakeEnvironment
        {
            IsLinux = true,
            ProcMemInfo = memInfo,
            ProcessorCount = 16
        };

        var profiler = new HardwareProfiler(probe, environment, new HardwareProfilerOptions());
        var profile = await profiler.GetProfileAsync(forceRefresh: false, CancellationToken.None);

        AssertEx.True(profile.VramKnown);
        AssertEx.Equal(24564L * Mib, profile.VramBytes!.Value);
        AssertEx.Null(profile.AvailableVramBytes);
    }

    [Test]
    public async Task HardwareProfiler_Linux_NvidiaSmi_SkipsLeadingNonNumericLine_ParsesVram()
    {
        // nvidia-smi can emit a leading warning banner before the numeric VRAM line; the profiler must scan past it
        // and parse the first PARSEABLE line rather than bail on the first non-empty line.
        const string memInfo = "MemTotal:       32830012 kB\nMemAvailable:   24000000 kB\n";
        var probe = new FakeProcessProbe()
                    .OnNvidiaName("NVIDIA GeForce RTX 4090\n")
                    .OnNvidiaMemoryTotal("WARNING: infoROM is corrupted\n24564\n");
        var environment = new FakeEnvironment
        {
            IsLinux = true,
            ProcMemInfo = memInfo,
            ProcessorCount = 16
        };

        var profiler = new HardwareProfiler(probe, environment, new HardwareProfilerOptions());
        var profile = await profiler.GetProfileAsync(forceRefresh: false, CancellationToken.None);

        AssertEx.Equal(GpuVendor.Nvidia, profile.GpuVendor);
        AssertEx.True(profile.VramKnown);
        AssertEx.Equal(24564L * Mib, profile.VramBytes!.Value);
        AssertEx.True(profile.GpuAccelAvailable);
    }

    [Test]
    public async Task HardwareProfiler_Linux_NoNvidia_UsesDrmVendor_AmdDegradesToCpu()
    {
        // AMD detected via /sys/class/drm vendor id, but Linux has no byte-accurate VRAM source → VRAM unknown ⇒ CPU mode.
        const string memInfo = "MemTotal:       16000000 kB\nMemAvailable:   12000000 kB\n";
        var probe = new FakeProcessProbe(); // nvidia-smi absent → returns null.
        var environment = new FakeEnvironment
        {
            IsLinux = true,
            ProcMemInfo = memInfo,
            DrmVendorIds = ["1002"],
            ProcessorCount = 8
        };

        var profiler = new HardwareProfiler(probe, environment, new HardwareProfilerOptions());
        var profile = await profiler.GetProfileAsync(forceRefresh: false, CancellationToken.None);

        AssertEx.Equal(GpuVendor.Amd, profile.GpuVendor);
        AssertEx.False(profile.VramKnown);
        AssertEx.Null(profile.VramBytes);
        AssertEx.False(profile.GpuAccelAvailable);
    }

    [Test]
    public async Task HardwareProfiler_Windows_ReportsVramAndVendor()
    {
        // NVIDIA on Windows is covered by the shared nvidia-smi branch → VRAM parsed, vendor NVIDIA, GPU accel available.
        var probe = new FakeProcessProbe()
                    .OnNvidiaName("NVIDIA RTX A2000\n")
                    .OnNvidiaMemoryTotal("6144\n");
        var environment = new FakeEnvironment
        {
            IsWindows = true,
            TotalRamBytes = 34_359_738_368L,
            AvailableRamBytes = 20_000_000_000L,
            ProcessorCount = 12
        };

        var profiler = new HardwareProfiler(probe, environment, new HardwareProfilerOptions());
        var profile = await profiler.GetProfileAsync(forceRefresh: false, CancellationToken.None);

        AssertEx.Equal(expected: 34_359_738_368L, profile.TotalRamBytes);
        AssertEx.Equal(GpuVendor.Nvidia, profile.GpuVendor);
        AssertEx.True(profile.VramKnown);
        AssertEx.Equal(6144L * Mib, profile.VramBytes!.Value);
        AssertEx.True(profile.GpuAccelAvailable);
    }

    [Test]
    public async Task HardwareProfiler_Windows_NonNvidia_DegradesToCpu_UntilDxgiSeamFilled()
    {
        // No NVIDIA; the DXGI/WMI Windows seam is not yet implemented (returns Unknown vendor + null VRAM on this box)
        // ⇒ the vendor-name fallback path yields VramKnown=false and CPU mode.
        var probe = new FakeProcessProbe(); // nvidia-smi absent.
        var environment = new FakeEnvironment
        {
            IsWindows = true,
            TotalRamBytes = 16_000_000_000L,
            AvailableRamBytes = 10_000_000_000L,
            ProcessorCount = 8
        };

        var profiler = new HardwareProfiler(probe, environment, new HardwareProfilerOptions());
        var profile = await profiler.GetProfileAsync(forceRefresh: false, CancellationToken.None);

        AssertEx.Equal(GpuVendor.Unknown, profile.GpuVendor);
        AssertEx.False(profile.VramKnown);
        AssertEx.False(profile.GpuAccelAvailable);
    }

    [Test]
    public async Task HardwareProfiler_WhenVramUnprobed_DegradesToCpu()
    {
        // No GPU detected at all → vendor None, VRAM unknown, GPU accel unavailable.
        const string memInfo = "MemTotal:        8000000 kB\nMemAvailable:    6000000 kB\n";
        var probe = new FakeProcessProbe();
        var environment = new FakeEnvironment
        {
            IsLinux = true,
            ProcMemInfo = memInfo,
            ProcessorCount = 4
        };

        var profiler = new HardwareProfiler(probe, environment, new HardwareProfilerOptions());
        var profile = await profiler.GetProfileAsync(forceRefresh: false, CancellationToken.None);

        AssertEx.Equal(GpuVendor.None, profile.GpuVendor);
        AssertEx.False(profile.VramKnown);
        AssertEx.Null(profile.VramBytes);
        AssertEx.False(profile.GpuAccelAvailable);
    }

    [Test]
    public async Task HardwareProfiler_CachesProfile_ReProbesOnForceRefresh()
    {
        const string memInfo = "MemTotal:        8000000 kB\nMemAvailable:    6000000 kB\n";
        var probe = new CountingProcessProbe();
        var environment = new FakeEnvironment
        {
            IsLinux = true,
            ProcMemInfo = memInfo,
            ProcessorCount = 4
        };
        var profiler = new HardwareProfiler(probe, environment, new HardwareProfilerOptions());

        await profiler.GetProfileAsync(forceRefresh: false, CancellationToken.None);
        await profiler.GetProfileAsync(forceRefresh: false, CancellationToken.None);
        var probesAfterCache = probe.CallCount;

        await profiler.GetProfileAsync(forceRefresh: true, CancellationToken.None);

        AssertEx.True(probesAfterCache > 0);
        AssertEx.True(probe.CallCount > probesAfterCache, "forceRefresh:true must re-probe the process seam.");
    }

    [Test]
    public void HardwareProfiler_NoHostAgentDependency_ExtractionGate()
    {
        // The profiler was extracted out of the now-removed in-Aspire HostAgent; this gate guards that it stays free of
        // any HostAgent.* dependency so the HostAgent can be deleted.
        var referencedAssemblies = typeof(HardwareProfiler).Assembly
                                                           .GetReferencedAssemblies()
                                                           .Select(name => name.Name ?? string.Empty)
                                                           .ToList();

        AssertEx.Empty(referencedAssemblies.Where(name => name.Contains("HostAgent", StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class FakeProcessProbe : IProcessProbe
    {
        private string? _nvidiaMemoryFree;
        private string? _nvidiaMemoryTotal;
        private string? _nvidiaName;

        public Task<ProcessProbeResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            if (fileName == "nvidia-smi" && arguments.Any(argument => argument.Contains("name", StringComparison.Ordinal)))
            {
                return Task.FromResult(_nvidiaName is null ? null : new ProcessProbeResult(ExitCode: 0, _nvidiaName));
            }

            if (fileName == "nvidia-smi" && arguments.Any(argument => argument.Contains("memory.total", StringComparison.Ordinal)))
            {
                return Task.FromResult(_nvidiaMemoryTotal is null ? null : new ProcessProbeResult(ExitCode: 0, _nvidiaMemoryTotal));
            }

            if (fileName == "nvidia-smi" && arguments.Any(argument => argument.Contains("memory.free", StringComparison.Ordinal)))
            {
                return Task.FromResult(_nvidiaMemoryFree is null ? null : new ProcessProbeResult(ExitCode: 0, _nvidiaMemoryFree));
            }

            return Task.FromResult<ProcessProbeResult?>(null);
        }

        public FakeProcessProbe OnNvidiaName(string stdout)
        {
            _nvidiaName = stdout;
            return this;
        }

        public FakeProcessProbe OnNvidiaMemoryTotal(string stdout)
        {
            _nvidiaMemoryTotal = stdout;
            return this;
        }

        public FakeProcessProbe OnNvidiaMemoryFree(string stdout)
        {
            _nvidiaMemoryFree = stdout;
            return this;
        }
    }

    private sealed class CountingProcessProbe : IProcessProbe
    {
        public int CallCount { get; private set; }

        public Task<ProcessProbeResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult<ProcessProbeResult?>(null);
        }
    }

    private sealed class FakeEnvironment : IHardwareProbeEnvironment
    {
        public string? ProcMemInfo { get; init; }

        public IReadOnlyList<string> DrmVendorIds { get; init; } = [];

        public long TotalRamBytes { get; init; }

        public long AvailableRamBytes { get; init; }

        public long FreeDiskBytes { get; init; }
        public bool IsWindows { get; init; }

        public bool IsLinux { get; init; }

        public int ProcessorCount { get; init; } = 1;

        public string? ReadProcMemInfo()
        {
            return ProcMemInfo;
        }

        public IReadOnlyList<string> ReadDrmVendorIds()
        {
            return DrmVendorIds;
        }

        public long GetTotalPhysicalMemoryBytes()
        {
            return TotalRamBytes;
        }

        public long GetAvailableMemoryBytes()
        {
            return AvailableRamBytes;
        }

        public long GetFreeDiskBytes(string path)
        {
            return FreeDiskBytes;
        }
    }
}
