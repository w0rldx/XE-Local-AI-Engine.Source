namespace XE_Local_AI_Engine.Client.Services.WebAccess.Implementation;

using System.Net;
using System.Text.Json;
using System.Web;
using AngleSharp.Html.Parser;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Runs one <c>web_search</c> query against DuckDuckGo's HTML endpoint, or against the operator's SearXNG instance
///     when one is configured, and returns title / URL / snippet results fenced as untrusted data.
/// </summary>
/// <remarks>
///     The kill switch is read here, as in <see cref="WebFetchService" />. DuckDuckGo goes through the guarded
///     <see cref="WebFetchService.HttpClientName" /> client and is best-effort (ADR 0017, D2): a bot challenge or a
///     refusal becomes <c>search-backend-unavailable</c>. SearXNG gets its own client WITHOUT the private-address deny
///     list, because the operator typed that URL and it is usually on localhost or the LAN; the model controls only
///     the query string, never the host or path. Queries are logged at Debug only.
/// </remarks>
internal sealed class WebSearchService
{
    /// <summary>The SearXNG client: no SSRF pin (operator-chosen host), no proxy, no cookies, no automatic redirects.</summary>
    public const string SearxngHttpClientName = "xe-web-search-searxng";

    public const string DuckDuckGoBackend = "duckduckgo";

    public const string SearxngBackend = "searxng";

    public const int MaxTitleChars = 200;

    public const int MaxSnippetChars = 400;

    internal static readonly Uri DuckDuckGoEndpoint = new("https://html.duckduckgo.com/html/");

    private const string UnavailableMessage =
        "Web search is temporarily unavailable (the search service refused the request). Try again later, or answer without searching.";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebSearchService> _logger;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly TimeProvider _timeProvider;

    public WebSearchService(IHttpClientFactory httpClientFactory,
        INodeRuntimeSettings runtimeSettings,
        TimeProvider timeProvider,
        ILogger<WebSearchService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Searches for <paramref name="query" /> into a typed outcome; <see cref="Serialize" /> renders it for the model.</summary>
    /// <param name="query">The model-supplied query.</param>
    /// <param name="maxResults">The model-supplied result count, clamped to 1..10; <see langword="null" /> means 5.</param>
    /// <param name="cancellationToken">The caller's cancellation.</param>
    public async Task<WebSearchOutcome> SearchAsync(string? query, int? maxResults, CancellationToken cancellationToken = default)
    {
        if (!await _runtimeSettings.GetWebAccessEnabledAsync(cancellationToken))
        {
            return WebSearchOutcome.Refused("web-access-disabled", "Web access is disabled on this node.");
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return WebSearchOutcome.Refused("invalid-query", "web_search requires a non-empty 'query'.");
        }

        query = query.Trim();
        var limit = Math.Clamp(maxResults ?? WebSearchToolDefinition.DefaultMaxResults, 1, WebSearchToolDefinition.MaxResultsLimit);
        var searxngUrl = await _runtimeSettings.GetWebSearchSearxngUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(searxngUrl))
        {
            searxngUrl = null;
        }

        var backend = searxngUrl is null ? DuckDuckGoBackend : SearxngBackend;
        _logger.LogDebug("web_search via {Backend}: {Query}", backend, query);

        // One time budget for both web tools: the node's WebFetchTimeoutSeconds.
        var timeBudget = await _runtimeSettings.GetWebFetchTimeoutAsync(cancellationToken);
        using var budget = new CancellationTokenSource(timeBudget, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            return searxngUrl is null
                ? await SearchDuckDuckGoAsync(query, limit, linked.Token)
                : await SearchSearxngAsync(searxngUrl, query, limit, linked.Token);
        }
        catch (HttpRequestException exception)
        {
            // Includes the DuckDuckGo client's pinned connect callback refusing a resolved address.
            _logger.LogDebug(exception, "web_search request to {Backend} failed.", backend);
            return WebSearchOutcome.Refused("request-failed", "The search request failed before a response arrived.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            // A connection reset mid-body, or a corrupt gzip/brotli body the handler failed to decode.
            _logger.LogDebug("web_search body from {Backend} could not be read: {ExceptionType}.", backend, exception.GetType().Name);
            return WebSearchOutcome.Refused("request-failed", "The search response body could not be read.");
        }
        catch (JsonException)
        {
            return WebSearchOutcome.Refused("search-failed", "The search service returned a response that could not be read.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WebSearchOutcome.Refused("timeout", $"The search did not answer within {timeBudget.TotalSeconds:0} seconds.");
        }
    }

    private async Task<WebSearchOutcome> SearchDuckDuckGoAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(WebFetchService.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, DuckDuckGoEndpoint);
        request.Content = new FormUrlEncodedContent([new("q", query)]);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // 202 is how the HTML endpoint serves its bot challenge; 403/429 and every other non-success mean the same to the model.
        if (response.StatusCode == HttpStatusCode.Accepted || !response.IsSuccessStatusCode)
        {
            return DuckDuckGoUnavailable($"HTTP {(int)response.StatusCode}");
        }

        var (html, _) = await WebFetchService.ReadCappedAsync(response.Content, cancellationToken);
        using var document = await new HtmlParser().ParseDocumentAsync(html, cancellationToken);
        if (document.QuerySelector("#challenge-form, .anomaly-modal__modal, form[action*='anomaly']") is not null)
        {
            return DuckDuckGoUnavailable("bot challenge");
        }

        var results = new List<WebSearchResult>(limit);
        foreach (var block in document.QuerySelectorAll(".result"))
        {
            if (results.Count == limit)
            {
                break;
            }

            if (block.ClassList.Contains("result--ad")
                || block.QuerySelector("a.result__a") is not { } anchor
                || ResolveDuckDuckGoHref(anchor.GetAttribute("href")) is not { } url)
            {
                continue;
            }

            results.Add(CreateResult(anchor.TextContent, url, block.QuerySelector(".result__snippet")?.TextContent));
        }

        return WebSearchOutcome.Found(DuckDuckGoBackend, results);
    }

    private WebSearchOutcome DuckDuckGoUnavailable(string reason)
    {
        _logger.LogInformation("DuckDuckGo refused a web_search ({Reason}). A SearXNG instance (Node settings, Web access) is the reliable search backend.", reason);
        return WebSearchOutcome.Refused("search-backend-unavailable", UnavailableMessage, DuckDuckGoBackend);
    }

    /// <summary>
    ///     A result link as the real target: the <c>//duckduckgo.com/l/?uddg=</c> click-through unwrapped, anything else on
    ///     DuckDuckGo's own host (<c>y.js</c> ads, settings) dropped, and only absolute http(s) kept.
    /// </summary>
    internal static string? ResolveDuckDuckGoHref(string? href)
    {
        if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(DuckDuckGoEndpoint, href.Trim(), out var url))
        {
            return null;
        }

        if (IsDuckDuckGoHost(url))
        {
            var target = string.Equals(url.AbsolutePath, "/l/", StringComparison.Ordinal) ? HttpUtility.ParseQueryString(url.Query)["uddg"] : null;
            if (target is null || !Uri.TryCreate(target, UriKind.Absolute, out url) || IsDuckDuckGoHost(url))
            {
                return null;
            }
        }

        return url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps ? url.AbsoluteUri : null;
    }

    private static bool IsDuckDuckGoHost(Uri url) =>
        string.Equals(url.Host, "duckduckgo.com", StringComparison.OrdinalIgnoreCase)
        || url.Host.EndsWith(".duckduckgo.com", StringComparison.OrdinalIgnoreCase);

    private async Task<WebSearchOutcome> SearchSearxngAsync(string baseUrl, string query, int limit, CancellationToken cancellationToken)
    {
        if (BuildSearxngUri(baseUrl, query) is not { } requestUri)
        {
            return WebSearchOutcome.Refused("search-backend-unavailable", "The configured SearXNG URL is not a valid absolute http(s) URL.", SearxngBackend);
        }

        var client = _httpClientFactory.CreateClient(SearxngHttpClientName);
        using var response = await client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            _logger.LogInformation("SearXNG answered HTTP 403 to a JSON search: 'json' is probably missing from search.formats in its settings.yml.");
            return WebSearchOutcome.Refused("search-backend-unavailable",
                "The SearXNG instance refused JSON output. The node operator must add 'json' to search.formats in the SearXNG settings.yml.",
                SearxngBackend);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogInformation("SearXNG answered HTTP {Status} to a web_search.", (int)response.StatusCode);
            return WebSearchOutcome.Refused("search-backend-unavailable", $"The SearXNG instance answered HTTP {(int)response.StatusCode}.", SearxngBackend);
        }

        var (body, _) = await WebFetchService.ReadCappedAsync(response.Content, cancellationToken);
        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("results", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return WebSearchOutcome.Refused("search-failed", "The SearXNG response carried no results list.", SearxngBackend);
        }

        var results = new List<WebSearchResult>(limit);
        foreach (var item in items.EnumerateArray())
        {
            if (results.Count == limit)
            {
                break;
            }

            if (item.ValueKind == JsonValueKind.Object
                && Uri.TryCreate(ReadString(item, "url"), UriKind.Absolute, out var url)
                && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
            {
                results.Add(CreateResult(ReadString(item, "title"), url.AbsoluteUri, ReadString(item, "content")));
            }
        }

        return WebSearchOutcome.Found(SearxngBackend, results);
    }

    /// <summary>
    ///     <c>{base}/search?q=…&amp;format=json</c>: host and path come only from the operator's URL, the query is escaped
    ///     into the <c>q</c> parameter, so no model input can change where the request goes.
    /// </summary>
    internal static Uri? BuildSearxngUri(string baseUrl, string query)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var builder = new UriBuilder(baseUri)
        {
            Path = baseUri.AbsolutePath.TrimEnd('/') + "/search",
            Query = "q=" + Uri.EscapeDataString(query) + "&format=json",
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static WebSearchResult CreateResult(string? title, string url, string? snippet) =>
        new()
        {
            Title = Clip(title, MaxTitleChars),
            Url = url,
            Snippet = Clip(snippet, MaxSnippetChars)
        };

    // Whitespace collapsed to single spaces (the HTML carries indentation and line breaks), then capped.
    private static string Clip(string? text, int maxChars)
    {
        var collapsed = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= maxChars ? collapsed : collapsed[..maxChars];
    }

    /// <summary>
    ///     The ONE step that turns an outcome into model-facing JSON: a refusal as <c>{ error, message }</c>, results as
    ///     one fence per hit holding its title, URL and snippet.
    /// </summary>
    /// <remarks>Only the backend name (ours) and the trust label sit outside the fences.</remarks>
    public static string Serialize(WebSearchOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (outcome.Results is not { } results)
        {
            return JsonSerializer.Serialize(new
                {
                    error = outcome.ErrorCode,
                    message = outcome.ErrorMessage
                },
                SerializerOptions);
        }

        var payload = new
        {
            backend = outcome.Backend,
            contentTrust = UntrustedContentFraming.UntrustedTrustLabel,
            results = results.Select(static result => UntrustedContentFraming.WrapDocument(result.Snippet,
            [
                new("title", result.Title),
                new("url", result.Url)
            ])).ToArray()
        };

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }
}
