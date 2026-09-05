namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Projects the shared process allocation onto the capacity admission contract.</summary>
public sealed class ModelFootprintProvider(
    IGpuVariantSelector variantSelector,
    IInferenceProfileResolver profileResolver,
    IProcessContextAllocationResolver allocationResolver) : IModelFootprintProvider
{
    private readonly IProcessContextAllocationResolver _allocationResolver =
        allocationResolver ?? throw new ArgumentNullException(nameof(allocationResolver));

    private readonly IInferenceProfileResolver _profileResolver =
        profileResolver ?? throw new ArgumentNullException(nameof(profileResolver));

    private readonly IGpuVariantSelector _variantSelector =
        variantSelector ?? throw new ArgumentNullException(nameof(variantSelector));

    public async Task<ModelFootprint> ResolveFootprintAsync(string modelName,
        ModelRole role,
        HardwareProfile profile,
        CancellationToken ct)
    {
        return await ResolveFootprintAsync(modelName, role, profile, requiredContextTokens: null, kvCacheType: null, ct).ConfigureAwait(false);
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

        var variant = await _variantSelector.SelectVariantAsync(ct).ConfigureAwait(false);
        var resolved = await _profileResolver.ResolveAsync(modelName, role, variant, ct).ConfigureAwait(false);
        var allocation = await _allocationResolver.ResolveAsync(modelName, role, variant, resolved, kvCacheType, ct).ConfigureAwait(false);
        // The free-VRAM reading rides along on the admission purely as a receipt: the capacity gate force-refreshed
        // the profile under its decision gate immediately before this call, so this is "free VRAM as of just before
        // the load" at zero extra cost. Nothing downstream may branch on it — the fit arithmetic stays in the gate.
        return allocation is null || requiredContextTokens is <= 0 || requiredContextTokens > allocation.ProcessContextTokens
            ? ModelFootprint.Unknown
            : ModelFootprint.Known(new ProcessLaunchAdmission(modelName,
                role,
                variant,
                resolved,
                allocation,
                profile.AvailableVramBytes));
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
