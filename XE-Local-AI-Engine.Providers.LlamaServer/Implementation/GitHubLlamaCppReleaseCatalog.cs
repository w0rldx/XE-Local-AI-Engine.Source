namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     <see cref="ILlamaCppReleaseCatalog" /> over the live <c>ggml-org/llama.cpp</c> GitHub Releases REST API.
/// </summary>
/// <remarks>
///     <para>
///         Singleton. Holds an in-memory ETag cache keyed by request URL: each entry stores the last <c>ETag</c> and the
///         parsed release payload. Conditional <c>If-None-Match</c> requests turn a <c>304 Not Modified</c> into a free
///         (uncounted) reuse of the cached parse; any other 2xx refreshes the cache.
///     </para>
///     <para>
///         GitHub requires a <c>User-Agent</c> header; one is sent on every request. Calls are unauthenticated (60/hr
///         per IP). A <c>403</c>/<c>429</c> with a rate-limit marker is treated as a sanitized "rate-limited" result and
///         the reset hint (<c>Retry-After</c> / <c>x-ratelimit-reset</c>) is recorded so a caller can back off; the
///         catalog never blocks or throws on it. Any network failure (DNS/connect/timeout) is an "offline" result.
///     </para>
/// </remarks>
public sealed partial class GitHubLlamaCppReleaseCatalog : ILlamaCppReleaseCatalog
{
    private const string Owner = "ggml-org";
    private const string Repo = "llama.cpp";
    private const string ApiBase = "https://api.github.com";
    private const string UserAgent = "XE-Local-AI-Engine-runtime-updater";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, CachedRelease> _cache = new(StringComparer.Ordinal);
    private readonly HttpClient _httpClient;

    /// <summary>Creates the catalog over the injected download/API <see cref="HttpClient" />.</summary>
    public GitHubLlamaCppReleaseCatalog(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    ///     The most recent rate-limit reset hint (UTC) observed, or <see langword="null" /> when not rate-limited. Lets a
    ///     caller surface a "try later" window without the catalog blocking. Best-effort, last-write-wins.
    /// </summary>
    public DateTimeOffset? RateLimitResetUtc { get; private set; }

    /// <inheritdoc />
    public async Task<LlamaCppReleaseResult> ResolveRecommendedAsync(string recommendedTag, CancellationToken ct)
    {
        if (!IsValidTag(recommendedTag))
        {
            return LlamaCppReleaseResult.NotFound();
        }

        var (release, signal) = await GetReleaseByTagAsync(recommendedTag, ct).ConfigureAwait(false);
        if (signal is not null)
        {
            return signal;
        }

        return release?.TagName is { Length: > 0 } tag
            ? LlamaCppReleaseResult.ForTag(tag)
            : LlamaCppReleaseResult.NotFound();
    }

    /// <inheritdoc />
    public async Task<LlamaCppReleaseResult> ResolveUpstreamLatestAsync(CancellationToken ct)
    {
        var requestUrl = $"{ApiBase}/repos/{Owner}/{Repo}/releases/latest";
        var (release, signal) = await GetReleaseAsync(requestUrl, ct).ConfigureAwait(false);
        if (signal is not null)
        {
            return signal;
        }

        return release?.TagName is { Length: > 0 } tag && IsValidTag(tag)
            ? LlamaCppReleaseResult.ForTag(tag)
            : LlamaCppReleaseResult.NotFound();
    }

    /// <inheritdoc />
    public async Task<LlamaCppReleaseResult> ResolveAssetAsync(string tag, OSPlatform os, Architecture arch, GpuVariant variant, CancellationToken ct)
    {
        if (!IsValidTag(tag))
        {
            return LlamaCppReleaseResult.NotFound();
        }

        var (release, signal) = await GetReleaseByTagAsync(tag, ct).ConfigureAwait(false);
        if (signal is not null)
        {
            return signal;
        }

        if (release?.Assets is not { Count: > 0 } assets)
        {
            return LlamaCppReleaseResult.NotFound();
        }

        var matched = MatchAsset(assets, tag, os, arch, variant);
        if (matched is null)
        {
            return LlamaCppReleaseResult.NotFound();
        }

        return LlamaCppReleaseResult.ForAsset(release.TagName ?? tag, matched);
    }

    /// <inheritdoc />
    public async Task<LlamaCppReleaseResult> ResolveCompanionAssetAsync(string tag, string assetName, CancellationToken ct)
    {
        if (!IsValidTag(tag) || !IsValidAssetName(assetName))
        {
            return LlamaCppReleaseResult.NotFound();
        }

        var (release, signal) = await GetReleaseByTagAsync(tag, ct).ConfigureAwait(false);
        if (signal is not null)
        {
            return signal;
        }

        if (release?.Assets is not { Count: > 0 } assets)
        {
            return LlamaCppReleaseResult.NotFound();
        }

        // Exact name match only — a companion archive has a known, derived name (no token/version fuzzing).
        var chosen = assets.FirstOrDefault(asset => string.Equals(asset.Name, assetName, StringComparison.OrdinalIgnoreCase));
        if (chosen is null || !IsValidAssetName(chosen.Name))
        {
            return LlamaCppReleaseResult.NotFound();
        }

        var digest = NormalizeDigest(chosen.Digest);
        if (digest is null || string.IsNullOrWhiteSpace(chosen.BrowserDownloadUrl)
                           || !Uri.TryCreate(chosen.BrowserDownloadUrl, UriKind.Absolute, out var downloadUrl))
        {
            // No usable digest → not safely installable; fall through so the caller fails clearly rather than installing blind.
            return LlamaCppReleaseResult.NotFound();
        }

        return LlamaCppReleaseResult.ForAsset(release.TagName ?? tag, new LlamaCppReleaseAsset(chosen.Name, downloadUrl, digest, chosen.Size));
    }

    /// <summary>
    ///     Templates the expected asset name from the pin scheme for the live tag, then matches it against the live
    ///     <c>assets[]</c>. A direct name match wins; otherwise it falls back to a token-based match (os/variant/arch
    ///     substrings) so a drifting CUDA version number does not break resolution. Returns the normalized asset (digest
    ///     stripped of its <c>sha256:</c> prefix) or <see langword="null" /> when nothing usable matches.
    /// </summary>
    private static LlamaCppReleaseAsset? MatchAsset(IReadOnlyList<GitHubAsset> assets, string tag, OSPlatform os, Architecture arch, GpuVariant variant)
    {
        var pin = LlamaCppReleasePins.Resolve(os, arch, variant);
        if (pin is null)
        {
            return null;
        }

        // Re-template the pinned asset name onto the requested tag (pin tag substring → requested tag).
        var expectedName = pin.AssetName.Replace(LlamaCppReleasePins.PinnedTag, tag, StringComparison.Ordinal);

        var exact = assets.FirstOrDefault(asset => string.Equals(asset.Name, expectedName, StringComparison.OrdinalIgnoreCase));
        var chosen = exact ?? assets.FirstOrDefault(asset => MatchesByTokens(asset.Name, expectedName));
        if (chosen is null)
        {
            return null;
        }

        // The asset name is a live-API value that downstream interpolates into a temp path + download URL. Gate it on the
        // file-name alphabet (no path/URL separators, no "..") before returning so a hostile/garbled name never escapes.
        if (!IsValidAssetName(chosen.Name))
        {
            return null;
        }

        var digest = NormalizeDigest(chosen.Digest);
        if (digest is null || string.IsNullOrWhiteSpace(chosen.BrowserDownloadUrl)
                           || !Uri.TryCreate(chosen.BrowserDownloadUrl, UriKind.Absolute, out var downloadUrl))
        {
            // An asset whose digest field is absent/unparseable is not safely installable — fall through a tier.
            return null;
        }

        return new LlamaCppReleaseAsset(chosen.Name, downloadUrl, digest, chosen.Size);
    }

    /// <summary>
    ///     Token match tolerant of CUDA-version drift: the candidate must carry the same archive extension and every
    ///     distinguishing token of the expected name except any purely-numeric version token (for example <c>12.4</c>).
    ///     Only the main asset is matched here; the Windows-CUDA <c>cudart-…</c> companion is resolved separately via
    ///     <see cref="ResolveCompanionAssetAsync" />.
    /// </summary>
    private static bool MatchesByTokens(string candidate, string expectedName)
    {
        var (expectedStem, expectedExt) = SplitArchive(expectedName);
        var (candidateStem, candidateExt) = SplitArchive(candidate);
        if (!string.Equals(expectedExt, candidateExt, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidateTokens = candidateStem.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in expectedStem.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (LooksLikeVersionToken(token))
            {
                continue;
            }

            if (!candidateTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool LooksLikeVersionToken(string token)
    {
        return token.Length > 0 && token.All(static c => char.IsDigit(c) || c == '.');
    }

    private static (string Stem, string Ext) SplitArchive(string name)
    {
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            return (name[..^".tar.gz".Length], ".tar.gz");
        }

        var dot = name.LastIndexOf('.');
        return dot < 0 ? (name, string.Empty) : (name[..dot], name[dot..]);
    }

    /// <summary>
    ///     Strips a leading <c>sha256:</c> prefix; rejects anything that is not 64 hex chars. Case is preserved (the
    ///     downstream hash comparison is case-insensitive), avoiding a culture-dependent case fold.
    /// </summary>
    private static string? NormalizeDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        var value = digest.Trim();
        const string prefix = "sha256:";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[prefix.Length..];
        }

        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value : null;
    }

    private async Task<(GitHubRelease? Release, LlamaCppReleaseResult? Signal)> GetReleaseByTagAsync(string tag, CancellationToken ct)
    {
        var requestUrl = $"{ApiBase}/repos/{Owner}/{Repo}/releases/tags/{tag}";
        return await GetReleaseAsync(requestUrl, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Conditional GET against the Releases API with the ETag cache. Returns the parsed release on success, or a
    ///     no-live-data signal (offline / rate-limited) the caller should propagate. A <c>304</c> reuses the cached
    ///     parse; a <c>404</c> returns <c>(null, null)</c> so the caller treats it as "not found" and falls through.
    /// </summary>
    private async Task<(GitHubRelease? Release, LlamaCppReleaseResult? Signal)> GetReleaseAsync(string requestUrl, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (_cache.TryGetValue(requestUrl, out var cached) && !string.IsNullOrEmpty(cached.ETag))
            {
                request.Headers.IfNoneMatch.ParseAdd(cached.ETag);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                return (cached.Release, null);
            }

            if (IsRateLimited(response))
            {
                RateLimitResetUtc = ReadRateLimitReset(response);
                return (null, LlamaCppReleaseResult.RateLimited());
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                // Any other non-success (5xx etc.) is treated as transient no-live-data, never thrown to the caller.
                return (null, LlamaCppReleaseResult.Offline());
            }

            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize<GitHubRelease>(payload, JsonOptions);
            if (release is null)
            {
                return (null, LlamaCppReleaseResult.NotFound());
            }

            var etag = response.Headers.ETag?.ToString();
            _cache[requestUrl] = new CachedRelease(etag, release, DateTimeOffset.UtcNow);
            return (release, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return (null, LlamaCppReleaseResult.Offline());
        }
        catch (TaskCanceledException)
        {
            // Request timeout (not caller cancellation) — treat as offline, no live data.
            return (null, LlamaCppReleaseResult.Offline());
        }
        catch (JsonException)
        {
            return (null, LlamaCppReleaseResult.NotFound());
        }
    }

    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        // GitHub returns 403 with x-ratelimit-remaining: 0 on the unauthenticated limit.
        if (response.StatusCode == HttpStatusCode.Forbidden
            && response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)
            && remaining.FirstOrDefault() == "0")
        {
            return true;
        }

        return false;
    }

    private static DateTimeOffset? ReadRateLimitReset(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta)
            {
                return DateTimeOffset.UtcNow + delta;
            }

            if (retryAfter.Date is { } date)
            {
                return date;
            }
        }

        if (response.Headers.TryGetValues("x-ratelimit-reset", out var resetValues)
            && long.TryParse(resetValues.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
        }

        return null;
    }

    /// <summary>Validates a release tag against the upstream <c>b&lt;N&gt;</c> scheme before it is composed into any URL.</summary>
    public static bool IsValidTag(string? tag)
    {
        return !string.IsNullOrWhiteSpace(tag) && TagRegex().IsMatch(tag);
    }

    /// <summary>
    ///     Allow-list gate on a live-API asset name before it is returned for download: only the file-name alphabet, no
    ///     path/URL separators or <c>..</c> traversal segments.
    /// </summary>
    private static bool IsValidAssetName(string? assetName)
    {
        return !string.IsNullOrWhiteSpace(assetName)
               && !assetName.Contains("..", StringComparison.Ordinal)
               && AssetNameRegex().IsMatch(assetName);
    }

    [GeneratedRegex(@"^b[0-9]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AssetNameRegex();

    private sealed record CachedRelease(string? ETag, GitHubRelease? Release, DateTimeOffset FetchedAtUtc);

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")]
        string? TagName,
        [property: JsonPropertyName("assets")]
        IReadOnlyList<GitHubAsset>? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")]
        string Name,
        [property: JsonPropertyName("browser_download_url")]
        string? BrowserDownloadUrl,
        [property: JsonPropertyName("digest")]
        string? Digest,
        [property: JsonPropertyName("size")]
        long Size);
}
