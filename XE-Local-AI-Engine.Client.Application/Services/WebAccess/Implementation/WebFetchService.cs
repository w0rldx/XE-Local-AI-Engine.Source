namespace XE_Local_AI_Engine.Client.Services.WebAccess.Implementation;

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using SmartReader;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Fetches one public web page for <c>web_fetch</c> and returns its readable text, fenced as untrusted data.
/// </summary>
/// <remarks>
///     The node kill switch is read HERE, not in the handler, so every caller is refused while web access is off
///     (the <c>ComputeToolGateway</c> rule). Every hop is validated by the custom-tool SSRF guard and dialled through
///     its pinned connect callback; redirects are followed by hand (at most <see cref="MaxRedirects" />) under ONE
///     time budget; the body is read as a capped stream. Bodies are never logged and URLs only at Debug: a query
///     string is exactly what an injected page would use to exfiltrate. ADR 0017.
/// </remarks>
internal sealed class WebFetchService
{
    /// <summary>The named client: pinned connect callback, no proxy, no cookies, no automatic redirects.</summary>
    public const string HttpClientName = "xe-web-fetch";

    // Deliberately not shared with the custom-tool HttpFetchExecutor: this is the built-in, model-driven fetch of any page, sized for readable text.
    public const int MaxRedirects = 5;

    public const int MaxBodyBytes = 2 * 1024 * 1024;

    /// <summary>The largest page, in elements, handed to SmartReader; see <see cref="ExtractHtmlAsync" />.</summary>
    internal const int MaxReadabilityElements = 8_000;

    /// <summary>The deepest element nesting handed to SmartReader; see <see cref="ExtractHtmlAsync" />.</summary>
    internal const int MaxReadabilityDepth = 64;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> AllowedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/html",
        "application/xhtml+xml",
        "text/plain",
        "text/markdown",
        "application/json"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebFetchService> _logger;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly TimeProvider _timeProvider;

    public WebFetchService(IHttpClientFactory httpClientFactory,
        INodeRuntimeSettings runtimeSettings,
        TimeProvider timeProvider,
        ILogger<WebFetchService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The model's <c>url</c> argument as the fetch sends it; the consent card shows this same form.</summary>
    internal static bool TryParseRequestUrl(string? url, [NotNullWhen(true)] out Uri? requested)
    {
        requested = null;
        return !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url.Trim(), UriKind.Absolute, out requested);
    }

    /// <summary>
    ///     The open-internet form of the custom-tool guard: no host allow-list, full private-address deny list.
    /// </summary>
    internal static void ValidatePublicUrl(Uri url) =>
        CustomToolSsrfGuard.ValidateRequestUrl(url, [], hostIsParameterized: false);

    /// <summary>
    ///     Whether <paramref name="url" /> matches an allow-list entry: same scheme, host and port, and a path under the
    ///     entry's path on a segment boundary (ADR 0017). An unparseable entry matches nothing, so junk denies all.
    /// </summary>
    internal static bool IsAllowed(Uri url, IReadOnlyList<string> allowedUrls)
    {
        foreach (var entry in allowedUrls)
        {
            if (Uri.TryCreate(entry?.Trim(), UriKind.Absolute, out var allowed)
                && string.Equals(allowed.Scheme, url.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(allowed.IdnHost, url.IdnHost, StringComparison.OrdinalIgnoreCase)
                && allowed.Port == url.Port
                && IsUnderPath(url.AbsolutePath, allowed.AbsolutePath))
            {
                return true;
            }
        }

        return false;
    }

    // AbsolutePath is percent- and dot-segment-normalized by Uri for http(s), so ".." cannot climb out of the prefix.
    // "/docs" admits "/docs" and "/docs/x" but not "/docs-private"; an entry ending in "/" (or the bare origin) admits all below it.
    private static bool IsUnderPath(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.Ordinal)
        && (path.Length == prefix.Length || prefix.EndsWith('/') || path[prefix.Length] == '/');

    /// <summary>Fetches <paramref name="url" /> into a typed page or refusal; <see cref="Serialize" /> renders it for the model.</summary>
    /// <param name="url">The model-supplied URL.</param>
    /// <param name="allowedUrls">
    ///     Optional link allow-list checked on the first URL and on every redirect hop; <see langword="null" /> means an
    ///     open fetch (chat and agents). The graph Tool-node path supplies it.
    /// </param>
    /// <param name="cancellationToken">The caller's cancellation.</param>
    public async Task<WebFetchOutcome> FetchAsync(string? url, IReadOnlyList<string>? allowedUrls, CancellationToken cancellationToken = default)
    {
        if (!await _runtimeSettings.GetWebAccessEnabledAsync(cancellationToken))
        {
            return WebFetchOutcome.Refused("web-access-disabled", "Web access is disabled on this node.");
        }

        if (!TryParseRequestUrl(url, out var requested))
        {
            return WebFetchOutcome.Refused("invalid-url", "web_fetch requires an absolute http or https 'url'.");
        }

        // Both limits are live node settings (WebFetchTimeoutSeconds, WebFetchMaxContentChars), read once per call.
        var timeBudget = await _runtimeSettings.GetWebFetchTimeoutAsync(cancellationToken);
        var maxContentChars = await _runtimeSettings.GetWebFetchMaxContentCharsAsync(cancellationToken);
        using var budget = new CancellationTokenSource(timeBudget, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            return await FetchCoreAsync(requested, allowedUrls, maxContentChars, linked.Token);
        }
        catch (CustomToolExecutionException exception)
        {
            return WebFetchOutcome.Refused("url-blocked", exception.Message);
        }
        catch (HttpRequestException exception) when (exception.InnerException is CustomToolExecutionException blocked)
        {
            // The pinned connect callback refused what the host name resolved to.
            return WebFetchOutcome.Refused("url-blocked", blocked.Message);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogDebug(exception, "web_fetch request to {Url} failed.", requested);
            return WebFetchOutcome.Refused("request-failed", "The request failed before a response arrived.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            // A connection reset mid-body, or a corrupt gzip/brotli body the handler failed to decode.
            _logger.LogDebug("web_fetch body from {Url} could not be read: {ExceptionType}.", requested, exception.GetType().Name);
            return WebFetchOutcome.Refused("request-failed", "The response body could not be read.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WebFetchOutcome.Refused("timeout", $"The page did not load within {timeBudget.TotalSeconds:0} seconds.");
        }
    }

    private async Task<WebFetchOutcome> FetchCoreAsync(Uri requested, IReadOnlyList<string>? allowedUrls, int maxContentChars, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var current = requested;
        var hop = 0;
        while (true)
        {
            ValidatePublicUrl(current);
            if (allowedUrls is not null && !IsAllowed(current, allowedUrls))
            {
                return WebFetchOutcome.Refused("url-not-allowed", hop == 0
                    ? "The URL is not in this node's allowed links."
                    : "The page redirected to a URL that is not in this node's allowed links.");
            }

            _logger.LogDebug("web_fetch GET {Url} (hop {Hop}).", current, hop);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (IsRedirect(response.StatusCode))
            {
                if (hop >= MaxRedirects)
                {
                    return WebFetchOutcome.Refused("too-many-redirects", $"The page redirected more than {MaxRedirects} times.");
                }

                if (response.Headers.Location is not { } location)
                {
                    return WebFetchOutcome.Refused("http-error", $"HTTP {(int)response.StatusCode} without a Location header.");
                }

                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                hop++;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return WebFetchOutcome.Refused("http-error", $"The server answered HTTP {(int)response.StatusCode}.");
            }

            // The set's own spelling, so the rendered type is canonical whatever case the server sent.
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !AllowedMediaTypes.TryGetValue(mediaType, out var canonicalMediaType))
            {
                return WebFetchOutcome.Refused("unsupported-content-type",
                    $"Content type '{mediaType ?? "none"}' is not supported; web_fetch reads HTML, plain text, Markdown and JSON only.");
            }

            var (body, bodyCapped) = await ReadCappedAsync(response.Content, cancellationToken);
            try
            {
                return WebFetchOutcome.Fetched(await BuildPageAsync(requested, current, canonicalMediaType, body, bodyCapped, maxContentChars, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A parser throw on hostile HTML is a refusal of this page, never a failure of the caller's turn.
                _logger.LogDebug("web_fetch could not extract text from {Url}: {ExceptionType}.", current, exception.GetType().Name);
                return WebFetchOutcome.Refused("extraction-failed", "The page could not be read as text.");
            }
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>Reads at most <see cref="MaxBodyBytes" /> of a body as text; <c>web_search</c> reads its backends through it too.</summary>
    internal static async Task<CappedBody> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken);
        await using (stream)
        {
            // Reads one byte past the cap, which tells a body of exactly MaxBodyBytes from a longer one.
            using var body = new MemoryStream();
            var chunk = new byte[81_920];
            while (body.Length <= MaxBodyBytes)
            {
                var wanted = (int)Math.Min(chunk.Length, MaxBodyBytes + 1 - body.Length);
                var read = await stream.ReadAsync(chunk.AsMemory(0, wanted), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await body.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }

            var capped = body.Length > MaxBodyBytes;
            return new CappedBody(ResolveEncoding(content).GetString(body.GetBuffer(), 0, (int)Math.Min(body.Length, MaxBodyBytes)), capped);
        }
    }

    // Only the encodings .NET ships without a code-page provider; anything else reads as UTF-8 rather than registering
    // a process-wide provider for one tool.
    private static Encoding ResolveEncoding(HttpContent content)
    {
        var charset = content.Headers.ContentType?.CharSet?.Trim('"', '\'', ' ');
        if (string.IsNullOrEmpty(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static async Task<WebFetchPage> BuildPageAsync(Uri requested, Uri final, string mediaType, string body, bool bodyCapped, int maxContentChars, CancellationToken cancellationToken)
    {
        var isHtml = mediaType is "text/html" or "application/xhtml+xml";
        var (title, text) = isHtml ? await ExtractHtmlAsync(final, body, cancellationToken) : new ExtractedText(null, body);
        text = CollapseBlankLines(text);

        var truncated = bodyCapped || text.Length > maxContentChars;
        if (text.Length > maxContentChars)
        {
            // Never split a surrogate pair: a lone high surrogate is not valid text.
            text = text[..(char.IsHighSurrogate(text[maxContentChars - 1]) ? maxContentChars - 1 : maxContentChars)];
        }

        return new WebFetchPage
        {
            Url = requested.AbsoluteUri,
            FinalUrl = final.AbsoluteUri,
            Title = title,
            ContentType = mediaType,
            Text = text,
            Truncated = truncated
        };
    }

    /// <summary>
    ///     The ONE step that turns an outcome into model-facing JSON: a refusal as <c>{ error, message }</c>, a page with
    ///     its body fenced as untrusted content.
    /// </summary>
    /// <remarks>
    ///     The final URL (a redirect target) and the title are server-controlled, so they travel INSIDE the fence with the
    ///     body; only the model's own URL and the allow-listed media type sit outside it.
    /// </remarks>
    public static string Serialize(WebFetchOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (outcome.Page is not { } page)
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
            url = page.Url,
            contentType = page.ContentType,
            contentTrust = UntrustedContentFraming.UntrustedTrustLabel,
            content = UntrustedContentFraming.WrapDocument(page.Text,
            [
                new("url", page.Url),
                new("finalUrl", page.FinalUrl),
                new("title", page.Title),
                new("contentType", page.ContentType)
            ]),
            truncated = page.Truncated
        };

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    internal readonly record struct CappedBody(string Text, bool Capped);

    /// <summary>
    ///     Main-content extraction: SmartReader's Readability port first, the whole visible body text when it finds no
    ///     article (a short page, a listing) or when the page is too large for it.
    /// </summary>
    /// <remarks>
    ///     SmartReader cannot be cancelled and its cost grows with element count and nesting depth (0.11.1: 16 KiB of
    ///     1,600-deep divs took 11 s, 1 MiB of flat divs 97 s). So AngleSharp's cancellable parse runs first and SmartReader
    ///     only sees a page within <see cref="MaxReadabilityElements" /> and <see cref="MaxReadabilityDepth" /> (worst
    ///     measured shape about 1 s); a larger page gets the body text.
    /// </remarks>
    internal readonly record struct ExtractedText(string? Title, string Text);

    internal static async Task<ExtractedText> ExtractHtmlAsync(Uri url, string html, CancellationToken cancellationToken)
    {
        using var document = await new HtmlParser().ParseDocumentAsync(html, cancellationToken);
        if (FitsReadability(document))
        {
            var article = ParseArticle(url, html);
            cancellationToken.ThrowIfCancellationRequested();
            if (article.IsReadable && !string.IsNullOrWhiteSpace(article.TextContent))
            {
                return new ExtractedText(NullIfBlank(article.Title), article.TextContent);
            }
        }

        foreach (var element in document.QuerySelectorAll("script, style, noscript, template"))
        {
            element.Remove();
        }

        return new ExtractedText(NullIfBlank(document.Title), document.Body?.TextContent ?? string.Empty);
    }

    private static bool FitsReadability(IHtmlDocument document) =>
        document.All.Length <= MaxReadabilityElements && document.All.All(static element => IsWithinReadabilityDepth(element));

    // Walks up with an early exit, so a hostile nesting costs at most MaxReadabilityDepth steps per element.
    private static bool IsWithinReadabilityDepth(IElement element)
    {
        var depth = 0;
        for (var parent = element.ParentElement; parent is not null; parent = parent.ParentElement)
        {
            if (++depth > MaxReadabilityDepth)
            {
                return false;
            }
        }

        return true;
    }

    // Synchronous on purpose: the reader already holds the page text, so this is in-memory parsing, and SmartReader's
    // async entry points are the ones that can download.
    private static Article ParseArticle(Uri url, string html)
    {
        using var reader = new Reader(url.AbsoluteUri, html);
#pragma warning disable MA0045 // See above: no I/O happens here.
        return reader.GetArticle();
#pragma warning restore MA0045
    }

    private static string CollapseBlankLines(string text)
    {
        var builder = new StringBuilder(text.Length);
        var blankRun = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                blankRun++;
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(blankRun > 0 ? "\n\n" : "\n");
            }

            builder.Append(line);
            blankRun = 0;
        }

        return builder.ToString();
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
