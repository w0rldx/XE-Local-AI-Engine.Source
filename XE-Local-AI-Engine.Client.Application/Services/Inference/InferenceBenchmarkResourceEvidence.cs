namespace XE_Local_AI_Engine.Client.Services.Inference;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

internal sealed class InferenceBenchmarkResourceSampler
{
    private readonly IHardwareProfiler _hardwareProfiler;
    private readonly IProcessVramBudgetProbe _processVramBudgetProbe;

    public InferenceBenchmarkResourceSampler(IHardwareProfiler hardwareProfiler, IProcessVramBudgetProbe processVramBudgetProbe)
    {
        _hardwareProfiler = hardwareProfiler;
        _processVramBudgetProbe = processVramBudgetProbe;
    }

    public async Task<ResourceObservation> CaptureAsync(InferenceBenchmarkSpec spec, int? processId, CancellationToken ct)
    {
        var hardware = await _hardwareProfiler.GetProfileAsync(forceRefresh: true, ct).ConfigureAwait(false);
        var processBudget = await _processVramBudgetProbe.TryGetProcessBudgetBytesAsync(spec.Backend, ct).ConfigureAwait(false);
        var globalFree = string.Equals(spec.Backend, InferenceBackends.Cpu, StringComparison.OrdinalIgnoreCase)
            ? null
            : hardware.AvailableVramBytes;

        return new ResourceObservation(VramObservation.Create(globalFree, processBudget), TryGetWorkingSetBytes(processId));
    }

    private static long? TryGetWorkingSetBytes(int? processId)
    {
        if (processId is not { } pid || pid <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Refresh();
            return process.HasExited ? null : process.WorkingSet64;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

internal sealed record VramObservation(
    long? GlobalFreeBytes,
    long? ProcessBudgetBytes,
    long? ProcessBudgetExcessBytes,
    double? ProcessBudgetExcessRatio,
    long? PressureAboveBaselineBytes,
    double? PressureAboveBaselineRatio,
    bool ExternalPressureDetected)
{
    public static VramObservation Create(long? globalFreeBytes, long? processBudgetBytes)
    {
        if (globalFreeBytes is not { } global || processBudgetBytes is not { } process || process <= global)
        {
            return new VramObservation(globalFreeBytes, processBudgetBytes, null, null, null, null, false);
        }

        var excess = process - global;
        var ratio = process > 0 ? (double)excess / process : 0d;
        return new VramObservation(globalFreeBytes, processBudgetBytes, excess, ratio, null, null, false);
    }
}

internal sealed record ResourceObservation(VramObservation Vram, long? WorkingSetBytes);

internal sealed class ResourceEvidenceCollector
{
    private readonly long _incrementalAbsoluteThresholdBytes;
    private readonly double _incrementalRatioThreshold;
    private readonly long _preSpawnAmbientBaselineBytes;
    private readonly long _preSpawnPressureAbsoluteThresholdBytes;
    private readonly double _preSpawnPressureRatioThreshold;
    private readonly bool _rejectPreSpawnVramPressure;
    private readonly List<ResourceObservation> _samples = [];

    public ResourceEvidenceCollector(LlamaServerProfilingVramSnapshot? preSpawnVram,
        long preSpawnAmbientBaselineBytes,
        long preSpawnPressureAbsoluteThresholdBytes,
        double preSpawnPressureRatioThreshold,
        bool rejectPreSpawnVramPressure,
        long incrementalAbsoluteThresholdBytes,
        double incrementalRatioThreshold)
    {
        _preSpawnAmbientBaselineBytes = Math.Max(0, preSpawnAmbientBaselineBytes);
        _preSpawnPressureAbsoluteThresholdBytes = Math.Max(0, preSpawnPressureAbsoluteThresholdBytes);
        _preSpawnPressureRatioThreshold = Math.Max(0d, preSpawnPressureRatioThreshold);
        _rejectPreSpawnVramPressure = rejectPreSpawnVramPressure;
        _incrementalAbsoluteThresholdBytes = Math.Max(0, incrementalAbsoluteThresholdBytes);
        _incrementalRatioThreshold = Math.Max(0d, incrementalRatioThreshold);
        PreSpawnVram = preSpawnVram is null
            ? null
            : ClassifyPreSpawnPressure(VramObservation.Create(preSpawnVram.GlobalFreeBytes, preSpawnVram.ProcessBudgetBytes));
    }

    public IReadOnlyList<ResourceObservation> Samples => _samples;
    public VramObservation? PreSpawnVram { get; }
    public bool ExternalPressureDetected => PreSpawnVram?.ExternalPressureDetected == true || _samples.Any(static sample => sample.Vram.ExternalPressureDetected);
    public ResourceObservation First => _samples[0];
    public ResourceObservation Last => _samples[^1];
    public long? PeakWorkingSetBytes => MaxNullable(_samples.Select(static sample => sample.WorkingSetBytes));
    public long? MinimumGlobalFreeBytes => MinNullable(_samples.Select(static sample => sample.Vram.GlobalFreeBytes));
    public long? MinimumProcessBudgetBytes => MinNullable(_samples.Select(static sample => sample.Vram.ProcessBudgetBytes));

    public void Add(ResourceObservation sample)
    {
        if (_samples.Count > 0
            && _samples[0].Vram.GlobalFreeBytes is not null
            && _samples[0].Vram.ProcessBudgetBytes is not null
            && sample.Vram.ProcessBudgetExcessBytes is { } currentExcess
            && sample.Vram.ProcessBudgetBytes is > 0)
        {
            var baselineExcess = _samples[0].Vram.ProcessBudgetExcessBytes ?? 0L;
            var additionalExcess = Math.Max(0L, currentExcess - baselineExcess);
            var additionalRatio = (double)additionalExcess / sample.Vram.ProcessBudgetBytes.Value;
            var material = additionalExcess >= _incrementalAbsoluteThresholdBytes && additionalRatio >= _incrementalRatioThreshold;
            sample = sample with
            {
                Vram = sample.Vram with
                {
                    PressureAboveBaselineBytes = additionalExcess,
                    PressureAboveBaselineRatio = additionalRatio,
                    ExternalPressureDetected = material
                }
            };
        }

        _samples.Add(sample);
    }

    private VramObservation ClassifyPreSpawnPressure(VramObservation observation)
    {
        if (observation.ProcessBudgetExcessBytes is not { } rawExcess || observation.ProcessBudgetBytes is not > 0)
        {
            return observation;
        }

        var pressureAboveBaseline = Math.Max(0L, rawExcess - _preSpawnAmbientBaselineBytes);
        var pressureAboveBaselineRatio = (double)pressureAboveBaseline / observation.ProcessBudgetBytes.Value;
        var material = _rejectPreSpawnVramPressure
                       && pressureAboveBaseline >= _preSpawnPressureAbsoluteThresholdBytes
                       && pressureAboveBaselineRatio >= _preSpawnPressureRatioThreshold;
        return observation with
        {
            PressureAboveBaselineBytes = pressureAboveBaseline,
            PressureAboveBaselineRatio = pressureAboveBaselineRatio,
            ExternalPressureDetected = material
        };
    }

    private static long? MaxNullable(IEnumerable<long?> values)
    {
        var present = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Max();
    }

    private static long? MinNullable(IEnumerable<long?> values)
    {
        var present = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Min();
    }
}
