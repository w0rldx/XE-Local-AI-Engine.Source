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

    public async Task<ModelFootprint> ResolveFootprintAsync(
        string modelName,
        ModelRole role,
        HardwareProfile profile,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(profile);

        var variant = await _variantSelector.SelectVariantAsync(ct).ConfigureAwait(false);
        var resolved = await _profileResolver.ResolveAsync(modelName, role, variant, ct).ConfigureAwait(false);
        var allocation = await _allocationResolver.ResolveAsync(modelName, role, variant, resolved, ct).ConfigureAwait(false);
        return allocation is null ? ModelFootprint.Unknown : ModelFootprint.Known(allocation.Footprint);
    }
}
