namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="ILlamaServerLaunchCapabilityInspector" />: resolves the same variant and binary a spawn would
///     use and answers from the cached <see cref="LlamaServerCapabilityManifest" /> the launch path already gates on,
///     so a caller's pre-launch decision and the launch itself are judged against one probe result.
/// </summary>
internal sealed class LlamaServerLaunchCapabilityInspector : ILlamaServerLaunchCapabilityInspector
{
    private readonly ILlamaCppBinaryManager _binaryManager;
    private readonly ILlamaServerCapabilityManifestProbe _manifestProbe;
    private readonly IGpuVariantSelector _variantSelector;

    public LlamaServerLaunchCapabilityInspector(IGpuVariantSelector variantSelector,
        ILlamaCppBinaryManager binaryManager,
        ILlamaServerCapabilityManifestProbe manifestProbe)
    {
        _variantSelector = variantSelector ?? throw new ArgumentNullException(nameof(variantSelector));
        _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
        _manifestProbe = manifestProbe ?? throw new ArgumentNullException(nameof(manifestProbe));
    }

    /// <inheritdoc />
    public async Task<LlamaServerLaunchCapabilities> InspectAsync(CancellationToken ct)
    {
        var variant = await _variantSelector.SelectVariantAsync(ct).ConfigureAwait(false);
        var binary = await _binaryManager.EnsureBinaryAsync(variant, ct).ConfigureAwait(false);
        var manifest = await _manifestProbe.GetManifestAsync(binary, ct).ConfigureAwait(false);

        return new LlamaServerLaunchCapabilities(variant,
            manifest.ProbeSucceeded,
            manifest.Version,
            manifest.ExecutableSha256,
            manifest.CacheTypesK,
            manifest.CacheTypesV,
            manifest.FlashAttentionModes)
        {
            SupportsAllValues = manifest.SupportsAllOptions
        };
    }
}
