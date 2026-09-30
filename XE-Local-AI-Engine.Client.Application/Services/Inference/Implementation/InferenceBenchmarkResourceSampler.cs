namespace XE_Local_AI_Engine.Client.Services.Inference.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

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
        var hardware = await _hardwareProfiler.GetProfileAsync(forceRefresh: true, ct);
        var processBudget = await _processVramBudgetProbe.TryGetProcessBudgetBytesAsync(spec.Backend, ct);
        var globalFree = string.Equals(spec.Backend, InferenceBackends.Cpu, StringComparison.OrdinalIgnoreCase)
            ? null
            : hardware.AvailableVramBytes;

        return new ResourceObservation
        {
            Vram = VramObservation.Create(globalFree, processBudget),
            WorkingSetBytes = TryGetWorkingSetBytes(processId)
        };
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
