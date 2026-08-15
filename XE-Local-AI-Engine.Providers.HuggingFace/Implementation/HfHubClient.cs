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

        return ListModelsAsync(new HubListQuery
            {
                Filter = "gguf",
                Sort = MapSort(query.Sort),
                Limit = query.Limit,
                SearchText = query.SearchText
            },
            ct);
    }

    /// <summary>
    ///     Lists repos for an arbitrary Hub facet (<see cref="HubListQuery.Filter" /> tag and/or
    ///     <see cref="HubListQuery.PipelineTag" />) using the same listing shape, cache and parsing as the GGUF search.
    ///     Image-model discovery rides this with <c>pipeline_tag=text-to-image</c>; the GGUF lane keeps <c>filter=gguf</c>.
    /// </summary>
    public Task<IReadOnlyList<HubModelSummary>> ListModelsAsync(HubListQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var url = BuildListUrl(query);
        return _searchCache.GetOrAddAsync(url, _options.HubMetadataCacheTtl, token => FetchModelsAsync(url, token), ct);
    }

    private async Task<IReadOnlyList<HubModelSummary>> FetchModelsAsync(string url, CancellationToken ct)
    {
        using var document = await GetJsonAsync(url, "model listing", ct).ConfigureAwait(false);
        if (document is null)
        {
            return [];
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("Hugging Face Hub model listing was not a JSON array.");
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

            summaries.Add(new HubModelSummary(repoId,
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
    public Task<HubModelDetail?> GetRepoAsync(string repoId, CancellationToken ct) =>
        GetRepoAsync(repoId, revision: null, ct);

    /// <summary>
    ///     The same detail read at a specific commit, branch or tag — <c>GET /api/models/{repo}/revision/{rev}?blobs=true</c>.
    ///     A blank revision reads the default branch, which is what the two-argument overload asks for. The revision is
    ///     part of the cache key AND escaped into its own path segment: it is untrusted repo input, and a branch name
    ///     like <c>refs/pr/1</c> is one segment to the Hub, not three.
    /// </summary>
    public Task<HubModelDetail?> GetRepoAsync(string repoId, string? revision, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);

        var url = string.IsNullOrWhiteSpace(revision)
            ? $"{TrimBase(_options.HubBaseUrl)}/api/models/{repoId}?blobs=true"
            : $"{TrimBase(_options.HubBaseUrl)}/api/models/{repoId}/revision/{Uri.EscapeDataString(revision)}?blobs=true";
        var cacheKey = string.IsNullOrWhiteSpace(revision) ? repoId : $"{repoId}@{revision}";
        return _repoDetailCache.GetOrAddAsync(cacheKey, _options.HubMetadataCacheTtl, token => FetchRepoAsync(url, repoId, token), ct);
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
                files.Add(new HubRepoFile(fileName, size, sha256));
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

    /// <summary>Maps the GGUF sort enum onto the Hub's <c>sort</c> query token.</summary>
    private static string MapSort(GgufSearchSort sort)
    {
        return sort switch
        {
            GgufSearchSort.Likes => "likes",
            GgufSearchSort.LastModified => "lastModified",
            GgufSearchSort.Downloads => "downloads",
            // Trending (the default): Hugging Face's recency-weighted popularity (the Hub "Trending" ranking). Lifetime
            // downloads is age-biased and surfaces years-old repos; trendingScore reflects current download/like velocity.
            _ => "trendingScore"
        };
    }

    private string BuildListUrl(HubListQuery query)
    {
        var limit = Math.Clamp(query.Limit, min: 1, max: 100);
        var builder = new StringBuilder();
        builder.Append(TrimBase(_options.HubBaseUrl));
        builder.Append("/api/models?");
        // filter=<tag> (tag-based) NOT library=<tag> — community repos (bartowski/unsloth) report library_name "None"
        // and would be under-matched by library=. pipeline_tag narrows by TASK (text-to-image) and is what makes image
        // discovery return diffusion repos instead of every GGUF on the Hub. Emitted before the shared parameters so
        // the GGUF listing URL keeps the exact shape its pin test froze.
        if (!string.IsNullOrWhiteSpace(query.Filter))
        {
            builder.Append("filter=");
            builder.Append(Uri.EscapeDataString(query.Filter));
            builder.Append('&');
        }

        if (!string.IsNullOrWhiteSpace(query.PipelineTag))
        {
            builder.Append("pipeline_tag=");
            builder.Append(Uri.EscapeDataString(query.PipelineTag));
            builder.Append('&');
        }

        // direction=-1 makes the popularity sort explicitly descending.
        builder.Append("full=true&direction=-1&sort=");
        builder.Append(query.Sort);
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
                names.Add(name);
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

    /// <summary>
    ///     One Hub listing request. <see cref="Filter" /> is a <b>tag</b> facet (<c>gguf</c>) and
    ///     <see cref="PipelineTag" /> a <b>task</b> facet (<c>text-to-image</c>); either, both or neither may be set.
    ///     <see cref="Sort" /> is the raw Hub token (<c>trendingScore|downloads|likes|lastModified</c>).
    /// </summary>
    internal sealed record HubListQuery
    {
        public string? Filter { get; init; }

        public string? PipelineTag { get; init; }

        public required string Sort { get; init; }

        public int Limit { get; init; } = 30;

        public string? SearchText { get; init; }
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
