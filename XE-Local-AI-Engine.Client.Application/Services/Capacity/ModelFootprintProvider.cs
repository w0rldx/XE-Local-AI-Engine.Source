namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Projects the shared process allocation onto the capacity admission contract.</summary>
public sealed class ModelFootprintProvider : IModelFootprintProvider
{
    private readonly IProcessContextAllocationResolver _allocationResolver;
    private readonly IInferenceProfileResolver _profileResolver;
    private readonly IGpuVariantSelector _variantSelector;

    public ModelFootprintProvider(IGpuVariantSelector variantSelector,
        IInferenceProfileResolver profileResolver,
        IProcessContextAllocationResolver allocationResolver)
    {
        ArgumentNullException.ThrowIfNull(allocationResolver);
        ArgumentNullException.ThrowIfNull(profileResolver);
        ArgumentNullException.ThrowIfNull(variantSelector);
        _allocationResolver = allocationResolver;
        _profileResolver = profileResolver;
        _variantSelector = variantSelector;
    }

    public async Task<ModelFootprint> ResolveFootprintAsync(string modelName,
        ModelRole role,
        HardwareProfile profile,
        CancellationToken ct)
    {
        return await ResolveFootprintAsync(modelName, role, profile, requiredContextTokens: null, kvCacheType: null, ct);
    }

    public async Task<ModelFootprint> ResolveFootprintAsync(string modelName,
        ModelRole role,
        HardwareProfile profile,
        int? requiredContextTokens,
        string? kvCacheType,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(profile);

        var variant = await _variantSelector.SelectVariantAsync(ct);
        var resolved = await _profileResolver.ResolveAsync(modelName, role, variant, ct);
        var allocation = await _allocationResolver.ResolveAsync(modelName, role, variant, resolved, kvCacheType, ct);
        // The free-VRAM reading rides along on the admission purely as a receipt: the gate force-refreshed the profile immediately before this call,
        // so it is "free VRAM as of just before the load" at zero extra cost. Nothing downstream may branch on it — the fit arithmetic stays in the gate.
        return allocation is null || requiredContextTokens is <= 0 || requiredContextTokens > allocation.ProcessContextTokens
            ? ModelFootprint.Unknown
            : ModelFootprint.Known(new ProcessLaunchAdmission
            {
                ModelName = modelName,
                Role = role,
                Variant = variant,
                ResolvedArguments = resolved,
                Allocation = allocation,
                GlobalFreeVramBytesAtAdmission = profile.AvailableVramBytes
            });
    }

    public bool TryDownTierForAdmission(ModelFootprint current, out ModelFootprint downTiered)
    {
        ArgumentNullException.ThrowIfNull(current);
        downTiered = current;
        if (current.Admission is null
            || !_allocationResolver.TryDownTierForAdmission(current.Admission.Allocation, out var adjusted))
        {
            return false;
        }

        downTiered = ModelFootprint.Known(current.Admission with
        {
            Allocation = adjusted
        });
        return true;
    }

    public bool TryCommitAdmissionFootprint(ModelFootprint candidate, out ModelFootprint committed)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        committed = candidate;
        if (candidate.Admission is null)
        {
            return candidate.IsKnown;
        }

        if (!_allocationResolver.TryCommitAdmissionAllocation(candidate.Admission.Allocation, out var allocation))
        {
            return false;
        }

        committed = ModelFootprint.Known(candidate.Admission with
        {
            Allocation = allocation
        });
        return true;
    }
}
