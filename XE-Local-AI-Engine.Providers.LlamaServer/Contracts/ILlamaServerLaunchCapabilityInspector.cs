namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     What the llama-server executable this node would launch right now actually accepts, as reported by the
///     executable itself. Nothing addressable is exposed: the binary is identified by variant, version and digest.
/// </summary>
/// <param name="Variant">The llama.cpp build the host selects.</param>
/// <param name="ProbeSucceeded">
///     Whether the executable could be interrogated at all. When <see langword="false" /> every support answer is
///     <see langword="false" /> and the caller must treat the surface as unknown rather than unsupported.
/// </param>
/// <param name="ExecutableVersion">The release the executable reported, or <see langword="null" />.</param>
/// <param name="ManifestSha256">The digest of the inspected executable, or <see langword="null" />.</param>
/// <param name="CacheTypesK">Element types the executable accepts for <c>-ctk</c>.</param>
/// <param name="CacheTypesV">Element types the executable accepts for <c>-ctv</c>.</param>
/// <param name="FlashAttentionModes">Values the executable accepts for <c>-fa</c>.</param>
public sealed record LlamaServerLaunchCapabilities(
    GpuVariant Variant,
    bool ProbeSucceeded,
    string? ExecutableVersion,
    string? ManifestSha256,
    IReadOnlySet<string> CacheTypesK,
    IReadOnlySet<string> CacheTypesV,
    IReadOnlySet<string> FlashAttentionModes)
{
    /// <summary>
    ///     Set only by the test-support manifest, which stands in for a binary whose full option surface is assumed.
    ///     A real probe always enumerates the values it parsed.
    /// </summary>
    internal bool SupportsAllValues { get; init; }

    /// <summary>Whether <paramref name="cacheType" /> is an accepted <c>-ctk</c> value.</summary>
    public bool SupportsCacheTypeK(string cacheType)
    {
        return SupportsAllValues || CacheTypesK.Contains(cacheType);
    }

    /// <summary>Whether <paramref name="cacheType" /> is an accepted <c>-ctv</c> value.</summary>
    public bool SupportsCacheTypeV(string cacheType)
    {
        return SupportsAllValues || CacheTypesV.Contains(cacheType);
    }

    /// <summary>Whether <paramref name="mode" /> is an accepted <c>-fa</c> value.</summary>
    public bool SupportsFlashAttentionMode(string mode)
    {
        return SupportsAllValues || FlashAttentionModes.Contains(mode);
    }
}

/// <summary>
///     Read-only capability question about the llama-server binary this node would launch, for callers that must decide
///     a launch vector BEFORE spawning anything — the benchmark freeze in particular, which has to reject a KV cache
///     type the selected binary cannot accept rather than discover it as a failed spawn.
/// </summary>
/// <remarks>
///     This is the public seam over the provider's internal capability manifest: it answers questions, and deliberately
///     exposes neither the manifest type nor the resolved binary (which carries a filesystem path).
/// </remarks>
public interface ILlamaServerLaunchCapabilityInspector
{
    /// <summary>
    ///     Selects the host's llama.cpp variant, ensures its binary, and reports that binary's accepted option surface.
    /// </summary>
    /// <exception cref="LlamaRuntimeException">The binary could not be acquired; the message is display-safe.</exception>
    Task<LlamaServerLaunchCapabilities> InspectAsync(CancellationToken ct);
}
