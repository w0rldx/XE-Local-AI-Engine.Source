namespace XE_Local_AI_Engine.Providers.Capabilities.Implementation;

using System.Globalization;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Capabilities.Contracts;
using XE_Local_AI_Engine.Providers.Capabilities.Options;

/// <summary>
///     Live <see cref="ILiveMemorySampler" />: RAM from <c>/proc/meminfo</c> or a fresh Windows reading, VRAM from one
///     <c>nvidia-smi</c> row per GPU.
/// </summary>
/// <remarks>
///     Independent of <see cref="HardwareProfiler" />, whose cache model fit and invalidation depend on: nothing here
///     reads or writes it. VRAM is system-wide usage, not llama.cpp's per-process <c>--list-devices</c> budget. A probe
///     serves every caller that arrives within <see cref="CacheWindow" /> of its start, in flight or complete.
/// </remarks>
internal sealed class LiveMemorySampler : ILiveMemorySampler
{
    private const string NvidiaSmi = "nvidia-smi";
    private const long Mib = 1024L * 1024L;

    private static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(2);

    private readonly IHardwareProbeEnvironment _environment;
    private readonly Lock _gate = new();
    private readonly HardwareProfilerOptions _options;
    private readonly IProcessProbe _processProbe;
    private readonly TimeProvider _timeProvider;

    private Task<LiveMemorySample>? _probe;
    private DateTimeOffset _probeStartedAtUtc;

    public LiveMemorySampler(IProcessProbe processProbe,
        IHardwareProbeEnvironment environment,
        HardwareProfilerOptions options,
        TimeProvider timeProvider)
    {
        _processProbe = processProbe ?? throw new ArgumentNullException(nameof(processProbe));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public Task<LiveMemorySample> SampleAsync(CancellationToken ct)
    {
        Task<LiveMemorySample> probe;
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (_probe is null || (_probe.IsCompleted && now - _probeStartedAtUtc >= CacheWindow))
            {
                _probe = ProbeAsync();
                _probeStartedAtUtc = now;
            }

            probe = _probe;
        }

        // The shared probe runs uncancelled, bounded by its own deadline, so one caller leaving never fails the others.
        return probe.WaitAsync(ct);
    }

    private async Task<LiveMemorySample> ProbeAsync()
    {
        var (totalRam, availableRam) = ReadRam();
        var gpus = await ProbeGpusAsync().ConfigureAwait(false);
        return new LiveMemorySample
        {
            TotalRamBytes = totalRam,
            AvailableRamBytes = availableRam,
            Gpus = gpus
        };
    }

    private (long TotalBytes, long AvailableBytes) ReadRam()
    {
        if (_environment.IsLinux
            && _environment.ReadProcMemInfo() is { } memInfo
            && HardwareProfiler.TryParseMemInfoKilobytes(memInfo, "MemTotal", out var totalKb)
            && HardwareProfiler.TryParseMemInfoKilobytes(memInfo, "MemAvailable", out var availableKb))
        {
            return (totalKb * 1024L, availableKb * 1024L);
        }

        // The GC-based figures are a last resort only: they refresh after a collection, so they cannot drive a live gauge.
        return _environment.ReadOsMemoryStatus()
               ?? (_environment.GetTotalPhysicalMemoryBytes(), _environment.GetAvailableMemoryBytes());
    }

    private async Task<IReadOnlyList<GpuMemorySample>> ProbeGpusAsync()
    {
        var result = await _processProbe
                           .RunAsync(NvidiaSmi,
                               ["--query-gpu=memory.total,memory.used,memory.free", "--format=csv,noheader,nounits"],
                               TimeSpan.FromSeconds(_options.HardwareProbeTimeoutSeconds),
                               CancellationToken.None)
                           .ConfigureAwait(false);

        // Missing tool, non-NVIDIA host, failed or timed-out probe: VRAM is unknown, which is an empty list, never zeros.
        return result is { TimedOut: false, ExitCode: 0 } ? ParseNvidiaCsv(result.StandardOutput) : [];
    }

    // Each GPU is one row of total, used and free MiB in device-index order. A banner line has no comma and takes no
    // index; a malformed or partial device row keeps its index but is dropped, so later devices keep their numbering.
    private static List<GpuMemorySample> ParseNvidiaCsv(string stdout)
    {
        var gpus = new List<GpuMemorySample>();
        var index = 0;
        foreach (var line in stdout.Split('\n'))
        {
            var columns = line.Split(',');
            if (columns.Length < 2)
            {
                continue;
            }

            var deviceIndex = index++;
            if (columns.Length == 3
                && TryParseMib(columns[0], out var total)
                && total > 0
                && TryParseMib(columns[1], out var used)
                && TryParseMib(columns[2], out var free))
            {
                gpus.Add(new GpuMemorySample
                {
                    Index = deviceIndex,
                    TotalVramBytes = total,
                    UsedVramBytes = used,
                    AvailableVramBytes = free
                });
            }
        }

        return gpus;
    }

    private static bool TryParseMib(string token, out long bytes)
    {
        if (long.TryParse(token.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var mib) && mib <= long.MaxValue / Mib)
        {
            bytes = mib * Mib;
            return true;
        }

        bytes = 0;
        return false;
    }
}
