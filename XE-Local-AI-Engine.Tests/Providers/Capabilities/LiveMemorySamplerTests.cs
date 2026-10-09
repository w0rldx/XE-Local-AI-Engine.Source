namespace XE_Local_AI_Engine.Tests.Providers.Capabilities;

using XE_Local_AI_Engine.Providers.Capabilities.Contracts;
using XE_Local_AI_Engine.Providers.Capabilities.Implementation;
using XE_Local_AI_Engine.Providers.Capabilities.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="LiveMemorySampler" /> tests: per-GPU <c>nvidia-smi</c> parsing, empty list on failure, the shared
///     two-second reading, one probe for concurrent callers, and an untouched profiler cache.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LiveMemorySamplerTests
{
    private const long Mib = 1024L * 1024L;
    private const string MemInfo = "MemTotal:       32000000 kB\nMemFree:         1000000 kB\nMemAvailable:   20000000 kB\n";

    [Test]
    public async Task Sample_ParsesEveryNvidiaRow_AndMemInfo()
    {
        var probe = new ScriptedProbe("24564, 4000, 20000\n49140, 9140, 40000\n");
        var sample = await CreateSampler(probe).SampleAsync(CancellationToken.None);

        AssertEx.Equal(32000000L * 1024, sample.TotalRamBytes);
        AssertEx.Equal(20000000L * 1024, sample.AvailableRamBytes);
        AssertEx.Equal(expected: 2, sample.Gpus.Count);
        AssertEx.Equal(expected: 0, sample.Gpus[0].Index);
        AssertEx.Equal(24564L * Mib, sample.Gpus[0].TotalVramBytes);
        AssertEx.Equal(4000L * Mib, sample.Gpus[0].UsedVramBytes);
        AssertEx.Equal(20000L * Mib, sample.Gpus[0].AvailableVramBytes);
        AssertEx.Equal(expected: 1, sample.Gpus[1].Index);
        AssertEx.Equal(49140L * Mib, sample.Gpus[1].TotalVramBytes);
        AssertEx.Equal(9140L * Mib, sample.Gpus[1].UsedVramBytes);
        AssertEx.Equal(40000L * Mib, sample.Gpus[1].AvailableVramBytes);
        var query = string.Join(separator: ' ', probe.LastArguments!);
        AssertEx.True(query.Contains("--query-gpu=memory.total,memory.used,memory.free", StringComparison.Ordinal), query);
        AssertEx.True(probe.LastTimeout > TimeSpan.Zero, "the probe must run under a finite deadline.");
    }

    [Test]
    public async Task Sample_DropsMalformedAndPartialRows_KeepingTheDriverIndexOfLaterDevices()
    {
        // A banner (no comma) takes no index; a partial row and an [N/A] row keep theirs but yield no entry.
        const string csv = "nvidia-smi WARNING: something\n24564, 4000\n[N/A], [N/A], [N/A]\n-1, 5, 5\n49140, 9140, 40000\n\n";
        var sample = await CreateSampler(new ScriptedProbe(csv)).SampleAsync(CancellationToken.None);

        var gpu = AssertEx.NotNull(sample.Gpus.SingleOrDefault());
        AssertEx.Equal(expected: 3, gpu.Index);
        AssertEx.Equal(49140L * Mib, gpu.TotalVramBytes);
    }

    [Test]
    public async Task Sample_WhenNvidiaSmiIsMissing_ReturnsAnEmptyDeviceList()
    {
        var sample = await CreateSampler(new ScriptedProbe(result: null)).SampleAsync(CancellationToken.None);

        AssertEx.Empty(sample.Gpus);
        AssertEx.Equal(32000000L * 1024, sample.TotalRamBytes);
    }

    [Test]
    public async Task Sample_WhenNvidiaSmiFails_ReturnsAnEmptyDeviceList()
    {
        var failed = new ProcessProbeResult
        {
            ExitCode = 9,
            StandardOutput = "24564, 4000, 20000\n"
        };

        var sample = await CreateSampler(new ScriptedProbe(failed)).SampleAsync(CancellationToken.None);

        AssertEx.Empty(sample.Gpus);
    }

    [Test]
    public async Task Sample_WhenNvidiaSmiTimesOut_ReturnsAnEmptyDeviceList()
    {
        var timedOut = new ProcessProbeResult
        {
            ExitCode = -1,
            StandardOutput = string.Empty,
            TimedOut = true
        };

        var sample = await CreateSampler(new ScriptedProbe(timedOut)).SampleAsync(CancellationToken.None);

        AssertEx.Empty(sample.Gpus);
    }

    [Test]
    public async Task Sample_WithoutMemInfo_UsesTheFreshOsReading()
    {
        var environment = new FakeEnvironment
        {
            ProcMemInfo = null,
            OsMemoryStatus = new OsMemoryStatus(16L * 1024 * Mib, 6L * 1024 * Mib),
            TotalRamBytes = 1,
            AvailableRamBytes = 1
        };

        var sample = await CreateSampler(new ScriptedProbe(result: null), environment).SampleAsync(CancellationToken.None);

        AssertEx.Equal(16L * 1024 * Mib, sample.TotalRamBytes);
        AssertEx.Equal(6L * 1024 * Mib, sample.AvailableRamBytes);
    }

    [Test]
    public async Task Sample_WithinTheCacheWindow_ReusesTheReading_ThenReprobesAfterIt()
    {
        var probe = new ScriptedProbe("24564, 4000, 20000\n");
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var sampler = CreateSampler(probe, clock: clock);

        var first = await sampler.SampleAsync(CancellationToken.None);
        clock.Advance(LiveMemorySampler.CacheWindow - TimeSpan.FromMilliseconds(1));
        var cached = await sampler.SampleAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, probe.CallCount);
        AssertEx.True(ReferenceEquals(first, cached), "a caller inside the window must get the shared reading.");

        clock.Advance(TimeSpan.FromMilliseconds(1));
        var fresh = await sampler.SampleAsync(CancellationToken.None);

        AssertEx.Equal(expected: 2, probe.CallCount);
        AssertEx.False(ReferenceEquals(first, fresh), "an expired reading must be re-probed.");
    }

    [Test]
    public async Task Sample_ConcurrentCallers_ShareOneInFlightProbe()
    {
        var probe = new ScriptedProbe("24564, 4000, 20000\n")
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var sampler = CreateSampler(probe);

        var callers = Enumerable.Range(0, 5).Select(_ => sampler.SampleAsync(CancellationToken.None)).ToArray();
        await AssertEx.StaysIncompleteAsync(Task.WhenAny(callers), "callers must wait on the gated probe.");
        probe.Gate.SetResult();
        var samples = await Task.WhenAll(callers);

        AssertEx.Equal(expected: 1, probe.CallCount);
        AssertEx.True(samples.All(sample => ReferenceEquals(sample, samples[0])), "every caller must get the one reading.");
    }

    [Test]
    public async Task Sample_ACallerLeaving_DoesNotCancelTheSharedProbe()
    {
        var probe = new ScriptedProbe("24564, 4000, 20000\n")
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var sampler = CreateSampler(probe);
        using var leaving = new CancellationTokenSource();

        var abandoned = sampler.SampleAsync(leaving.Token);
        var staying = sampler.SampleAsync(CancellationToken.None);
        await leaving.CancelAsync();
        probe.Gate.SetResult();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => abandoned);
        AssertEx.Equal(expected: 1, (await staying).Gpus.Count);
        AssertEx.Equal(expected: 1, probe.CallCount);
    }

    [Test]
    public async Task Sample_LeavesTheHardwareProfilerCacheUntouched()
    {
        var probe = new ScriptedProbe("NVIDIA Test GPU, 24564, 20000\n");
        var environment = new FakeEnvironment();
        var profiler = new HardwareProfiler(probe, environment, new HardwareProfilerOptions());
        var profile = await profiler.GetProfileAsync(forceRefresh: false, CancellationToken.None);

        probe.Output = "24564, 23000, 1564\n";
        var sample = await CreateSampler(probe, environment).SampleAsync(CancellationToken.None);
        var afterSample = await profiler.GetProfileAsync(forceRefresh: false, CancellationToken.None);

        AssertEx.Equal(1564L * Mib, sample.Gpus[0].AvailableVramBytes);
        AssertEx.True(ReferenceEquals(profile, afterSample), "the sampler must not replace the cached profile.");
        AssertEx.Equal(20000L * Mib, afterSample.AvailableVramBytes!.Value);
    }

    private static LiveMemorySampler CreateSampler(ScriptedProbe probe,
        FakeEnvironment? environment = null,
        ManualTimeProvider? clock = null)
    {
        return new LiveMemorySampler(probe, environment ?? new FakeEnvironment(), new HardwareProfilerOptions(), clock ?? new ManualTimeProvider(DateTimeOffset.UnixEpoch));
    }

    /// <summary>Answers every nvidia-smi call with one scripted result, optionally held behind a test-controlled gate.</summary>
    private sealed class ScriptedProbe : IProcessProbe
    {
        private readonly ProcessProbeResult? _fixedResult;
        private readonly bool _useFixedResult;
        private int _callCount;

        public ScriptedProbe(string output)
        {
            Output = output;
        }

        public ScriptedProbe(ProcessProbeResult? result)
        {
            _fixedResult = result;
            _useFixedResult = true;
        }

        public string Output { get; set; } = string.Empty;

        public TaskCompletionSource? Gate { get; init; }

        public int CallCount => Volatile.Read(ref _callCount);

        public IReadOnlyList<string>? LastArguments { get; private set; }

        public TimeSpan LastTimeout { get; private set; }

        public async Task<ProcessProbeResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            AssertEx.Equal("nvidia-smi", fileName);
            Interlocked.Increment(ref _callCount);
            LastArguments = arguments;
            LastTimeout = timeout;
            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(ct);
            }

            return _useFixedResult
                ? _fixedResult
                : new ProcessProbeResult
                {
                    ExitCode = 0,
                    StandardOutput = Output
                };
        }
    }

    private sealed class FakeEnvironment : IHardwareProbeEnvironment
    {
        public OsMemoryStatus? OsMemoryStatus { get; init; }

        public string? ProcMemInfo { get; init; } = MemInfo;

        public long TotalRamBytes { get; init; }

        public long AvailableRamBytes { get; init; }

        public bool IsWindows => false;

        public bool IsLinux => ProcMemInfo is not null;

        public int ProcessorCount => 1;

        public string? ReadProcMemInfo()
        {
            return ProcMemInfo;
        }

        public IReadOnlyList<string> ReadDrmVendorIds()
        {
            return [];
        }

        public long GetTotalPhysicalMemoryBytes()
        {
            return TotalRamBytes;
        }

        public long GetAvailableMemoryBytes()
        {
            return AvailableRamBytes;
        }

        public OsMemoryStatus? ReadOsMemoryStatus()
        {
            return OsMemoryStatus;
        }

        public long GetFreeDiskBytes(string path)
        {
            return 0;
        }
    }
}
