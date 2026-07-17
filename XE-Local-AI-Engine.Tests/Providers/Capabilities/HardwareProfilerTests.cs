namespace XE_Local_AI_Engine.Tests.Providers.Capabilities;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Capabilities.Contracts;
using XE_Local_AI_Engine.Providers.Capabilities.Implementation;
using XE_Local_AI_Engine.Providers.Capabilities.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="HardwareProfiler" /> tests: Windows and Linux RAM/VRAM/vendor detection, the degrade-to-CPU path when
///     VRAM cannot be probed, the SINGLE consolidated <c>nvidia-smi</c> query (name+total+free, multi-GPU first-wins,
///     malformed-line skipping), the bounded-probe degrade (a killed/timed-out probe reuses the cached profile or the
///     CPU default and records the timeout metric), and the gate proving the profiler project carries no HostAgent
///     dependency. The process + environment probe seams are faked so detection is exercised with canned output and NO
///     real GPU, process spawn or platform pin.
/// </summary>
public sealed class HardwareProfilerTests
{
    private const long Mib = 1024L * 1024L;
    private const long Kb = 1024L;

    [Test]
    public async Task HardwareProfiler_Linux_ParsesMemInfoAndNvidiaSmi()
    {
        const string memInfo = "MemTotal:       32830012 kB\nMemFree:         1000000 kB\nMemAvailable:   24000000 kB\n";
        var probe = new FakeProcessProbe().WithNvidiaCsv("NVIDIA GeForce RTX 4090, 24564, 8192\n");
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
    public async Task HardwareProfiler_Nvidia_SingleInvocation_ConsolidatedQuery()
    {
        // AUD4-07: the former three sequential nvidia-smi calls (name, memory.total, memory.free) collapse into ONE
        // consolidated query — one process spawn per probe, all three fields in a single csv line.
        var probe = new FakeProcessProbe().WithNvidiaCsv("NVIDIA GeForce RTX 4090, 24564, 8192\n");
        var environment = new FakeEnvironment
        {
            IsLinux = true,
            ProcMemInfo = "MemTotal: 8 kB\nMemAvailable: 4 kB\n",
            ProcessorCount = 8
        };

        var profiler = new HardwareProfiler(probe, environment, new HardwareProfilerOptions());
        await profiler.GetProfileAsync(forceRefresh: false, CancellationToken.None);

        AssertEx.Equal(expected: 1, probe.NvidiaCallCount);
        AssertEx.NotNull(probe.LastArguments);
        var query = string.Join(separator: ' ', probe.LastArguments!);
        AssertEx.True(query.Contains("name,memory.total,memory.free", StringComparison.Ordinal),
            "the single query must request name+total+free together.");
        AssertEx.True(query.Contains("noheader", StringComparison.Ordinal) && query.Contains("nounits", StringComparison.Ordinal),
            "csv,noheader,nounits keeps the figures bare integers.");
    }

    [Test]
    public async Task HardwareProfiler_Nvidia_ParsesFreeVram_AsAvailableVramBytes()
    {
        // nvidia-smi memory.free yields the free-VRAM baseline the capacity gate uses; it is reported in MiB like total.
        const string memInfo = "MemTotal:       32830012 kB\nMemAvailable:   24000000 kB\n";
        var probe = new FakeProcessProbe().WithNvidiaCsv("NVIDIA GeForce RTX 4090, 24564, 8192\n");
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
    public async Task HardwareProfiler_Nvidia_WhenFreeColumnAbsent_LeavesAvailableVramNull()
    {
        // A row that carries only name+total (no free column) yields VramBytes but AvailableVramBytes null, so the
        // capacity gate falls back to total-VRAM math rather than trusting a missing free figure.
        const string memInfo = "MemTotal:       32830012 kB\nMemAvailable:   24000000 kB\n";
        var probe = new FakeProcessProbe().WithNvidiaCsv("NVIDIA GeForce RTX 4090, 24564\n"); // no free column.
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
    public async Task HardwareProfiler_Nvidia_TwoGpus_FirstGpuWins()
    {
        // Multi-GPU emits one csv line per device; the first GPU's figures win (matching the former per-field probes'
        // "first parseable figure" semantics).
        const string memInfo = "MemTotal:       32830012 kB\nMemAvailable:   24000000 kB\n";
        var probe = new FakeProcessProbe().WithNvidiaCsv("NVIDIA GeForce RTX 4090, 24564, 8192\nNVIDIA RTX A6000, 49140, 40000\n");
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
    public async Task HardwareProfiler_Nvidia_SkipsBannerAndMalformedRows_ParsesFirstUsableGpu()
    {
        // A leading warning banner (no csv columns) and a row whose total is unparseable are both scanned past; the first
        // row carrying a parseable total wins.
        const string memInfo = "MemTotal:       32830012 kB\nMemAvailable:   24000000 kB\n";
        var probe = new FakeProcessProbe()
            .WithNvidiaCsv("WARNING: infoROM is corrupted\nNVIDIA GeForce RTX 4090, [N/A], [N/A]\nNVIDIA GeForce RTX 4090, 24564, 8192\n");
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
        AssertEx.Equal(8192L * Mib, profile.AvailableVramBytes!.Value);
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
        var probe = new FakeProcessProbe().WithNvidiaCsv("NVIDIA RTX A2000, 6144, 6000\n");
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
    public async Task HardwareProfiler_ConfiguredTimeout_IsPassedToTheProcessProbe()
    {
        var probe = new FakeProcessProbe().WithNvidiaCsv("NVIDIA GeForce RTX 4090, 24564, 8192\n");
        var environment = new FakeEnvironment
        {
            IsLinux = true,
            ProcMemInfo = "MemTotal: 8 kB\nMemAvailable: 4 kB\n",
            ProcessorCount = 8
        };
        var options = new HardwareProfilerOptions
        {
            HardwareProbeTimeoutSeconds = 7
        };

        var profiler = new HardwareProfiler(probe, environment, options);
        await profiler.GetProfileAsync(forceRefresh: false, CancellationToken.None);

        AssertEx.Equal(TimeSpan.FromSeconds(7), probe.LastTimeout);
    }

    [Test]
    public async Task HardwareProfiler_ProbeTimeout_NoCache_DegradesToCpuDefault_AndRecordsMetric()
    {
        // A wedged nvidia-smi is killed and reported as timed-out; with no prior good profile the profiler degrades to
        // the CPU-safe default (VRAM unknown ⇒ CPU mode) and records the timeout metric — never hangs.
        const string memInfo = "MemTotal:       32000000 kB\nMemAvailable:   24000000 kB\n";
        var probe = new FakeProcessProbe().WithTimeout();
        var environment = new FakeEnvironment
        {
            IsLinux = true,
            ProcMemInfo = memInfo,
            ProcessorCount = 16
        };
        var metrics = new FakeHardwareProbeMetrics();

        var profiler = new HardwareProfiler(probe, environment, new HardwareProfilerOptions(), metrics: metrics);
        var profile = await profiler.GetProfileAsync(forceRefresh: false, CancellationToken.None);

        AssertEx.False(profile.VramKnown, "a timed-out GPU probe with no cache must degrade to CPU mode.");
        AssertEx.False(profile.GpuAccelAvailable);
        AssertEx.Equal(24000000L * Kb, profile.AvailableRamBytes); // RAM read is unaffected by the GPU probe timeout.
        AssertEx.Equal(expected: 1, metrics.TimeoutCount);
        AssertEx.Equal("nvidia-smi", metrics.LastProbe);
    }

    [Test]
    public async Task HardwareProfiler_ProbeTimeout_DegradesToLastCachedProfile()
    {
        // First probe succeeds (real GPU), a later forced refresh times out → the profiler reuses the cached GPU profile
        // (keeping its real VRAM figures) rather than dropping to CPU mode, and still records the timeout metric.
        const string memInfo = "MemTotal:       32000000 kB\nMemAvailable:   24000000 kB\n";
        var probe = new FakeProcessProbe().WithNvidiaCsv("NVIDIA GeForce RTX 4090, 24564, 8192\n").TimeoutAfterFirstCall();
        var environment = new FakeEnvironment
        {
            IsLinux = true,
            ProcMemInfo = memInfo,
            ProcessorCount = 16
        };
        var metrics = new FakeHardwareProbeMetrics();

        var profiler = new HardwareProfiler(probe, environment, new HardwareProfilerOptions(), metrics: metrics);

        var first = await profiler.GetProfileAsync(forceRefresh: false, CancellationToken.None);
        AssertEx.True(first.VramKnown);

        var refreshed = await profiler.GetProfileAsync(forceRefresh: true, CancellationToken.None);

        AssertEx.True(refreshed.VramKnown, "the forced refresh must reuse the last good profile when the probe times out.");
        AssertEx.Equal(24564L * Mib, refreshed.VramBytes!.Value);
        AssertEx.Equal(GpuVendor.Nvidia, refreshed.GpuVendor);
        AssertEx.Equal(expected: 1, metrics.TimeoutCount);
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
        private string? _nvidiaCsv;
        private bool _timeout;
        private bool _timeoutAfterFirstCall;

        public int NvidiaCallCount { get; private set; }

        public TimeSpan LastTimeout { get; private set; }

        public IReadOnlyList<string>? LastArguments { get; private set; }

        public Task<ProcessProbeResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            if (fileName != "nvidia-smi")
            {
                return Task.FromResult<ProcessProbeResult?>(null);
            }

            NvidiaCallCount++;
            LastTimeout = timeout;
            LastArguments = arguments;

            if (_timeout || (_timeoutAfterFirstCall && NvidiaCallCount > 1))
            {
                return Task.FromResult<ProcessProbeResult?>(new ProcessProbeResult(ExitCode: -1, StandardOutput: string.Empty, TimedOut: true));
            }

            return Task.FromResult(_nvidiaCsv is null ? null : new ProcessProbeResult(ExitCode: 0, _nvidiaCsv));
        }

        public FakeProcessProbe WithNvidiaCsv(string csv)
        {
            _nvidiaCsv = csv;
            return this;
        }

        public FakeProcessProbe WithTimeout()
        {
            _timeout = true;
            return this;
        }

        public FakeProcessProbe TimeoutAfterFirstCall()
        {
            _timeoutAfterFirstCall = true;
            return this;
        }
    }

    private sealed class CountingProcessProbe : IProcessProbe
    {
        public int CallCount { get; private set; }

        public Task<ProcessProbeResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult<ProcessProbeResult?>(null);
        }
    }

    private sealed class FakeHardwareProbeMetrics : IHardwareProbeMetrics
    {
        public int TimeoutCount { get; private set; }

        public string? LastProbe { get; private set; }

        public void RecordProbeTimeout(string probe)
        {
            TimeoutCount++;
            LastProbe = probe;
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
