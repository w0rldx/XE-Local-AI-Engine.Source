namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

/// <summary>
///     Typed wrapper over the Hugging Face Hub REST list/tree endpoints feeding <see cref="HuggingFaceGgufDiscovery" />.
///     Anonymous for public listing; tolerant JSON parsing (Hub field variance) via <see cref="JsonDocument" /> — unknown
///     fields are ignored and a missing optional field is never fatal. Internal — exercised in tests via a stubbed
///     <see cref="HttpMessageHandler" />.
/// </summary>
/// <remarks>
///     Hub facts (verified live 2026-06-18; sort confirmed 2026-06-26): listing is
///     <c>GET /api/models?filter=gguf&amp;sort=trendingScore&amp;limit=N&amp;full=true</c> (<c>sort</c> is one of
///     <c>trendingScore|downloads|likes|lastModified</c>)
///     returning <c>id</c>, <c>gated</c> (<see langword="false" /> | <c>"auto"</c> | <c>"manual"</c>), <c>downloads</c>,
///     <c>likes</c>, <c>lastModified</c>, and <c>siblings[].rfilename</c> (filenames only in the listing). Per-repo detail
///     is <c>GET /api/models/{repo}?blobs=true</c> returning <c>sha</c> (resolved commit), <c>cardData.license</c>, and
///     <c>siblings[]</c> with <c>rfilename</c>, <c>size</c>, <c>blobId</c>, and <c>lfs.sha256</c>/<c>lfs.size</c> for LFS blobs.
/// </remarks>
internal sealed class HfHubClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HfHubClient> _logger;
    private readonly HuggingFaceOptions _options;
    private readonly TtlCache<IReadOnlyList<HubModelSummary>> _searchCache;
    private readonly TtlCache<HubModelDetail?> _repoDetailCache;

    public HfHubClient(HttpClient httpClient, HuggingFaceOptions options, ILogger<HfHubClient> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _options = options;
        _logger = logger;
        _searchCache = new TtlCache<IReadOnlyList<HubModelSummary>>(timeProvider);
        _repoDetailCache = new TtlCache<HubModelDetail?>(timeProvider);
    }

    /// <summary>
    ///     Lists GGUF repos (<c>?filter=gguf</c>) sorted by popularity. Returns the raw summaries the Hub exposes in the
    ///     listing; per-file inspection (sizes, header metadata) happens later via <see cref="GetRepoAsync" />. Cached for
    ///     <see cref="HuggingFaceOptions.HubMetadataCacheTtl" />, keyed by the fully-built listing URL (sort/limit/search
    ///     all included), so repeated advisor refreshes with the same query reuse one fetch.
    /// </summary>
    public Task<IReadOnlyList<HubModelSummary>> ListGgufModelsAsync(GgufSearchQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var url = BuildListUrl(query);
        return _searchCache.GetOrAddAsync(url, _options.HubMetadataCacheTtl, token => FetchGgufModelsAsync(url, token), ct);
    }

    private async Task<IReadOnlyList<HubModelSummary>> FetchGgufModelsAsync(string url, CancellationToken ct)
    {
        using var document = await GetJsonAsync(url, "GGUF listing", ct).ConfigureAwait(false);
        if (document is null)
        {
            return [];
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("Hugging Face Hub GGUF listing was not a JSON array.");
            return [];
        }

        var summaries = new List<HubModelSummary>(root.GetArrayLength());
        foreach (var element in root.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var repoId = GetString(element, "id");
            if (string.IsNullOrWhiteSpace(repoId))
            {
                continue;
            }

            summaries.Add(new HubModelSummary(repoId!,
                ParseGated(element),
                GetInt64(element, "downloads") ?? 0L,
                (int)(GetInt64(element, "likes") ?? 0L),
                GetDate(element, "lastModified") ?? DateTimeOffset.MinValue,
                ExtractLicense(element),
                ReadSiblingFileNames(element)));
        }

        return summaries;
    }

    /// <summary>
    ///     Fetches one repo's detail with per-file blob metadata (<c>?blobs=true</c>): resolved commit <c>sha</c>, gating,
    ///     license, and each sibling's filename/size/LFS sha256. Returns <see langword="null" /> on a non-success status.
    ///     Cached for <see cref="HuggingFaceOptions.HubMetadataCacheTtl" />, keyed by repo id.
    /// </summary>
    public Task<HubModelDetail?> GetRepoAsync(string repoId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);

        var url = $"{TrimBase(_options.HubBaseUrl)}/api/models/{repoId}?blobs=true";
        return _repoDetailCache.GetOrAddAsync(repoId, _options.HubMetadataCacheTtl, token => FetchRepoAsync(url, repoId, token), ct);
    }

    private async Task<HubModelDetail?> FetchRepoAsync(string url, string repoId, CancellationToken ct)
    {
        using var document = await GetJsonAsync(url, "repo inspection", ct).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            _logger.LogWarning("Hugging Face Hub repo detail was not a JSON object.");
            return null;
        }

        var files = new List<HubRepoFile>();
        if (root.TryGetProperty("siblings", out var siblings) && siblings.ValueKind == JsonValueKind.Array)
        {
            foreach (var sibling in siblings.EnumerateArray())
            {
                if (sibling.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var fileName = GetString(sibling, "rfilename");
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                // LFS metadata is the reliable size/hash source for GGUF blobs; the top-level size is a fallback.
                long? lfsSize = null;
                string? sha256 = null;
                if (sibling.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object)
                {
                    lfsSize = GetInt64(lfs, "size");
                    sha256 = GetString(lfs, "sha256");
                }

                var size = lfsSize ?? GetInt64(sibling, "size") ?? 0L;
                files.Add(new HubRepoFile(fileName!, size, sha256));
            }
        }

        return new HubModelDetail(GetString(root, "id") ?? repoId,
            ParseGated(root),
            ExtractLicense(root),
            GetString(root, "sha") ?? string.Empty,
            files);
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, string context, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Hugging Face Hub {Context} returned 404.", context);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Hugging Face Hub {Context} returned {StatusCode}.", context, (int)response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse Hugging Face Hub {Context} response.", context);
            return null;
        }
    }

    private string BuildListUrl(GgufSearchQuery query)
    {
        var sort = query.Sort switch
        {
            GgufSearchSort.Likes => "likes",
            GgufSearchSort.LastModified => "lastModified",
            GgufSearchSort.Downloads => "downloads",
            // Trending (the default): Hugging Face's recency-weighted popularity (the Hub "Trending" ranking). Lifetime
            // downloads is age-biased and surfaces years-old repos; trendingScore reflects current download/like velocity.
            _ => "trendingScore"
        };

        var limit = Math.Clamp(query.Limit, min: 1, max: 100);
        var builder = new StringBuilder();
        builder.Append(TrimBase(_options.HubBaseUrl));
        // filter=gguf (tag-based) NOT library=gguf — community repos (bartowski/unsloth) report library_name "None"
        // and would be under-matched by library=. direction=-1 makes the popularity sort explicitly descending.
        builder.Append("/api/models?filter=gguf&full=true&direction=-1&sort=");
        builder.Append(sort);
        builder.Append("&limit=");
        builder.Append(limit.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            builder.Append("&search=");
            builder.Append(Uri.EscapeDataString(query.SearchText));
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> ReadSiblingFileNames(JsonElement model)
    {
        if (!model.TryGetProperty("siblings", out var siblings) || siblings.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var names = new List<string>(siblings.GetArrayLength());
        foreach (var sibling in siblings.EnumerateArray())
        {
            if (sibling.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = GetString(sibling, "rfilename");
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name!);
            }
        }

        return names;
    }

    private static bool ParseGated(JsonElement model)
    {
        // HF returns false | "auto" | "manual"; anything other than literal false / "false" means access is gated.
        if (!model.TryGetProperty("gated", out var gated))
        {
            return false;
        }

        return gated.ValueKind switch
        {
            JsonValueKind.False => false,
            JsonValueKind.True => true,
            JsonValueKind.String => !string.Equals(gated.GetString(), "false", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static string? ExtractLicense(JsonElement model)
    {
        if (model.TryGetProperty("cardData", out var card) &&
            card.ValueKind == JsonValueKind.Object &&
            card.TryGetProperty("license", out var license) &&
            license.ValueKind == JsonValueKind.String)
        {
            return license.GetString();
        }

        return null;
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static long? GetInt64(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt64(out var number)
            ? number
            : null;
    }

    private static DateTimeOffset? GetDate(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String &&
               value.TryGetDateTimeOffset(out var date)
            ? date
            : null;
    }

    private static string TrimBase(string baseUrl)
    {
        return baseUrl.TrimEnd('/');
    }

    /// <summary>A repo as it appears in the Hub GGUF listing (filenames only, no per-file size).</summary>
    internal sealed record HubModelSummary(
        string RepoId,
        bool IsGated,
        long Downloads,
        int Likes,
        DateTimeOffset LastModified,
        string? License,
        IReadOnlyList<string> FileNames);

    /// <summary>One repo's inspected detail with per-file blob metadata.</summary>
    internal sealed record HubModelDetail(
        string RepoId,
        bool IsGated,
        string? License,
        string Revision,
        IReadOnlyList<HubRepoFile> Files);

    /// <summary>One sibling file's resolved size + optional LFS sha256.</summary>
    internal sealed record HubRepoFile(string FileName, long SizeBytes, string? Sha256);
}
