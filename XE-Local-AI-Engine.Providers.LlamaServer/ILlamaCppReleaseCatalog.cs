namespace XE_Local_AI_Engine.Providers.LlamaServer;

using System.Runtime.InteropServices;

/// <summary>
///     One resolved release asset from the live GitHub Releases API: the per-variant archive plus its publisher digest.
/// </summary>
/// <param name="Name">The release asset file name (for example <c>llama-b9700-bin-win-vulkan-x64.zip</c>).</param>
/// <param name="DownloadUrl">The asset's direct download URL (<c>browser_download_url</c>).</param>
/// <param name="Digest">
///     The asset's GitHub-published digest, normalized to a lowercase hex SHA256 (the <c>sha256:</c> prefix stripped).
/// </param>
/// <param name="Size">The asset size in bytes as reported by the API.</param>
public sealed record LlamaCppReleaseAsset(string Name, Uri DownloadUrl, string Digest, long Size);

/// <summary>
///     A resolved release-catalog lookup. Carries either a successful payload (a resolved tag and/or asset) or a
///     graceful no-live-data signal (<see cref="IsOffline" /> / <see cref="IsRateLimited" />) — the catalog NEVER throws
///     into a caller's happy path so the 3-tier resolve can fall through to the disk cache and the pinned floor.
/// </summary>
/// <param name="Tag">The resolved release tag, when a tag was requested/resolved; otherwise <see langword="null" />.</param>
/// <param name="Asset">The resolved asset, when an asset was requested and matched; otherwise <see langword="null" />.</param>
/// <param name="IsOffline">
///     <see langword="true" /> when the live API was unreachable (network error / DNS / timeout) — no live data.
/// </param>
/// <param name="IsRateLimited">
///     <see langword="true" /> when the API returned a rate-limit response and the lookup was backed off — no live data.
/// </param>
public sealed record LlamaCppReleaseResult(
    string? Tag,
    LlamaCppReleaseAsset? Asset,
    bool IsOffline,
    bool IsRateLimited)
{
    /// <summary>A successful tag-only resolution (no asset requested).</summary>
    public static LlamaCppReleaseResult ForTag(string tag)
    {
        return new LlamaCppReleaseResult(tag, Asset: null, IsOffline: false, IsRateLimited: false);
    }

    /// <summary>A successful tag + asset resolution.</summary>
    public static LlamaCppReleaseResult ForAsset(string tag, LlamaCppReleaseAsset asset)
    {
        return new LlamaCppReleaseResult(tag, asset, IsOffline: false, IsRateLimited: false);
    }

    /// <summary>The live API was unreachable — fall through to the next acquisition tier.</summary>
    public static LlamaCppReleaseResult Offline()
    {
        return new LlamaCppReleaseResult(Tag: null, Asset: null, IsOffline: true, IsRateLimited: false);
    }

    /// <summary>The live API rate-limited the request — back off, fall through to the next tier.</summary>
    public static LlamaCppReleaseResult RateLimited()
    {
        return new LlamaCppReleaseResult(Tag: null, Asset: null, IsOffline: false, IsRateLimited: true);
    }

    /// <summary>The request succeeded but no matching data was found (no such tag/asset) — fall through.</summary>
    public static LlamaCppReleaseResult NotFound()
    {
        return new LlamaCppReleaseResult(Tag: null, Asset: null, IsOffline: false, IsRateLimited: false);
    }

    /// <summary>True when this result carries no usable live payload (offline, rate-limited, or empty).</summary>
    public bool HasNoLiveData => Tag is null && Asset is null;
}

/// <summary>
///     Resolves llama.cpp release tags and per-variant assets against the live <c>ggml-org/llama.cpp</c> GitHub Releases
///     API. The single source of dynamic version truth — demotes the compiled-in <see cref="LlamaCppReleasePins" /> to an
///     offline floor.
/// </summary>
/// <remarks>
///     <para>
///         All methods fail gracefully: an unreachable API, a rate-limit response, or a missing release returns a
///         no-live-data <see cref="LlamaCppReleaseResult" /> rather than throwing into the caller's happy path, so the
///         3-tier resolve (live → cached <c>installed-runtime.json</c> → pins) can fall through.
///     </para>
///     <para>
///         Conditional <c>If-None-Match</c> requests are used (a <c>304</c> reuses the cached parse and is free against
///         the unauthenticated rate limit); <c>Retry-After</c> / <c>x-ratelimit-reset</c> are honored on rate-limit.
///     </para>
/// </remarks>
public interface ILlamaCppReleaseCatalog
{
    /// <summary>
    ///     Resolves the recommended tag, validating it against <c>^b\d+$</c> and confirming it exists upstream. Returns a
    ///     tag-only result on success, or a no-live-data result when the API is unreachable/rate-limited or the tag is
    ///     malformed/absent.
    /// </summary>
    Task<LlamaCppReleaseResult> ResolveRecommendedAsync(string recommendedTag, CancellationToken ct);

    /// <summary>
    ///     Resolves the true upstream <c>latest</c> release tag (for developer mode). Returns a tag-only result, or a
    ///     no-live-data result when the API is unreachable/rate-limited.
    /// </summary>
    Task<LlamaCppReleaseResult> ResolveUpstreamLatestAsync(CancellationToken ct);

    /// <summary>
    ///     Resolves the asset for a concrete <paramref name="tag" /> and the host <paramref name="os" />/
    ///     <paramref name="arch" />/<paramref name="variant" /> by templating the expected name from the pin scheme and
    ///     matching it against the live <c>assets[]</c> to read the publisher digest. Returns a tag+asset result, or a
    ///     no-live-data result when unreachable/rate-limited, the tag is malformed/absent, or no asset matches.
    /// </summary>
    Task<LlamaCppReleaseResult> ResolveAssetAsync(string tag, OSPlatform os, Architecture arch, GpuVariant variant, CancellationToken ct);

    /// <summary>
    ///     Resolves a named companion asset (an exact <paramref name="assetName" /> match) within a concrete
    ///     <paramref name="tag" /> and reads its publisher digest — used to verify the Windows-CUDA <c>cudart-…</c> runtime
    ///     archive the SAME way the main asset's live digest is resolved. Returns a tag+asset result, or a no-live-data
    ///     result when unreachable/rate-limited, the tag/name is malformed/absent, or the asset's digest is unusable.
    /// </summary>
    Task<LlamaCppReleaseResult> ResolveCompanionAssetAsync(string tag, string assetName, CancellationToken ct);
}
