namespace XE_Local_AI_Engine.Client.Services.CustomTools.Implementation;

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Executes an <c>HttpFetch</c> custom tool: substitutes the model's arguments into the URL/body, runs the SSRF
///     guard on the final URL, sends the request through the SSRF-pinned named client (auto-redirects OFF), and returns
///     a secret-scrubbed, size-bounded summary of the response. Secret header values and the URL/userinfo are scrubbed
///     from both the model-facing result and anything logged. The send + body read is bounded by a fixed wall-clock
///     timeout and admitted through the same <see cref="CustomToolConcurrencyLimiter" /> the command path uses, so a
///     fan-out of concurrent fetches is capped the same way a fan-out of concurrent host commands is.
/// </summary>
internal sealed class HttpFetchExecutor : ICustomToolExecutor
{
    /// <summary>The named <see cref="HttpClient" /> whose handler carries the SSRF connect-pin and has redirects disabled.</summary>
    public const string HttpClientName = "xe-custom-tool-fetch";

    private const int MaxResponseBodyBytes = 64 * 1024;

    // Wall-clock ceiling for the send + body read, mirroring the command path's default timeout (HostProcessExecutor's
    // DefaultTimeoutSeconds, itself clamped to a 1-300s bound). A fetch tool has no per-call timeout config to clamp,
    // so this is a fixed default rather than a derived clamp.
    private const int FetchTimeoutSeconds = 30;

    // Response headers that carry credentials/session material must never be surfaced to the model.
    private static readonly HashSet<string> StrippedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Set-Cookie", "Set-Cookie2", "WWW-Authenticate", "Proxy-Authenticate", "Authorization"
    };

    private static readonly HashSet<string> AllowedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CustomToolConcurrencyLimiter _concurrencyLimiter;
    private readonly ILogger<HttpFetchExecutor> _logger;

    public HttpFetchExecutor(IHttpClientFactory httpClientFactory, CustomToolConcurrencyLimiter concurrencyLimiter, ILogger<HttpFetchExecutor> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _concurrencyLimiter = concurrencyLimiter ?? throw new ArgumentNullException(nameof(concurrencyLimiter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public CustomToolKind Kind => CustomToolKind.HttpFetch;

    public async Task<string> ExecuteAsync(CustomToolRecord tool, string jsonArguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);

        // Definite-assigned to the userinfo-only redactor so every catch below — including one thrown before config
        // parses — has a real (if minimal) redactor to scrub through, never an unredacted raw message.
        var redactor = new SecretValueRedactor([]);
        try
        {
            var config = CustomToolConfigParser.ParseHttpFetch(tool.ConfigJson);
            var parameters = CustomToolConfigParser.ParseParameters(tool.ParametersJson);
            redactor = BuildRedactor(config);

            using var request = BuildRequest(config, parameters, jsonArguments);
            using var slot = await _concurrencyLimiter.AcquireAsync(cancellationToken).ConfigureAwait(false);
            using var timeoutSource = new CancellationTokenSource();
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(FetchTimeoutSeconds));

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedSource.Token).ConfigureAwait(false);
            return await FormatResponseAsync(response, redactor, linkedSource.Token).ConfigureAwait(false);
        }
        catch (CustomToolExecutionException exception)
        {
            // A guard blocked the call (SSRF, bad template, type mismatch). Return a scrubbed, non-throwing result.
            return $"The custom tool call was blocked: {redactor.Redact(exception.Message)}";
        }
        catch (CustomToolConfigurationException exception)
        {
            _logger.LogWarning("Custom tool {ToolName} has invalid configuration: {Reason}", tool.Name, exception.Message);
            return "The custom tool is misconfigured and could not run.";
        }
        catch (HttpRequestException exception)
        {
            return $"The custom tool request failed: {redactor.Redact(exception.Message)}";
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "The custom tool request timed out.";
        }
    }

    private static SecretValueRedactor BuildRedactor(HttpFetchConfig config)
    {
        return new SecretValueRedactor(config.Headers.Where(static header => header.IsSecret).Select(static header => header.Value));
    }

    private static HttpRequestMessage BuildRequest(HttpFetchConfig config,
        IReadOnlyList<CustomToolParameter> parameters,
        string jsonArguments)
    {
        if (!AllowedMethods.Contains(config.Method))
        {
            throw new CustomToolExecutionException($"The HTTP method '{config.Method}' is not allowed.");
        }

        var bound = CustomToolTemplate.BindAndEnforce(jsonArguments, parameters);
        var declaredNames = parameters.Select(static parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);

        var url = BuildUrl(config, bound, declaredNames);

        var request = new HttpRequestMessage(new HttpMethod(config.Method.ToUpperInvariant()), url);
        try
        {
            // Build the body first so a content-typed header (e.g. Content-Type) routes onto the content that exists.
            if (!string.IsNullOrEmpty(config.BodyTemplate))
            {
                // Body values are inserted verbatim (no URL-encoding — a body is not a URL); undeclared placeholders
                // still fail closed.
                var body = CustomToolTemplate.Substitute(config.BodyTemplate, bound, declaredNames);
                request.Content = new StringContent(body, Encoding.UTF8);
            }

            foreach (var header in config.Headers)
            {
                AddHeader(request, header.Name, header.Value);
            }

            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private static void AddHeader(HttpRequestMessage request, string name, string value)
    {
        // A content header (Content-Type, …) is rejected by request.Headers; route it to the content instead.
        if (!request.Headers.TryAddWithoutValidation(name, value))
        {
            request.Content?.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static Uri BuildUrl(HttpFetchConfig config,
        IReadOnlyDictionary<string, string> bound,
        IReadOnlySet<string> declaredNames)
    {
        var hostIsParameterized = HostSectionIsParameterized(config.UrlTemplate);

        // URL-encode every substituted value so a value can only fill a single path segment or query value — never
        // introduce a new authority ('@host'), a scheme, an extra path, or a query break-out.
        var assembled = CustomToolTemplate.Substitute(config.UrlTemplate, bound, declaredNames, Uri.EscapeDataString);

        if (!Uri.TryCreate(assembled, UriKind.Absolute, out var url))
        {
            throw new CustomToolExecutionException("The assembled request URL is not a valid absolute URL.");
        }

        CustomToolSsrfGuard.ValidateRequestUrl(url, config.AllowedHosts, hostIsParameterized);
        return url;
    }

    private static bool HostSectionIsParameterized(string urlTemplate)
    {
        // The authority is between "://" and the first '/', '?', or '#'. A placeholder there means the model can fill
        // the host, which forces the allowedHosts requirement. A placeholder in the scheme is illegal too.
        var schemeSeparator = urlTemplate.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
        {
            throw new CustomToolExecutionException("The URL template must be an absolute http(s) URL.");
        }

        if (urlTemplate.AsSpan(0, schemeSeparator).Contains('{'))
        {
            throw new CustomToolExecutionException("The URL scheme must not be parameterized.");
        }

        var authorityStart = schemeSeparator + 3;
        var authorityEnd = urlTemplate.Length;
        for (var index = authorityStart; index < urlTemplate.Length; index++)
        {
            var character = urlTemplate[index];
            if (character is '/' or '?' or '#')
            {
                authorityEnd = index;
                break;
            }
        }

        return urlTemplate.AsSpan(authorityStart, authorityEnd - authorityStart).Contains('{');
    }

    private static async Task<string> FormatResponseAsync(HttpResponseMessage response,
        SecretValueRedactor redactor,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        builder.Append('\n');

        AppendSafeHeaders(builder, response.Headers);
        AppendSafeHeaders(builder, response.Content.Headers);

        var body = await ReadCappedBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
        builder.Append('\n');
        builder.Append(body);

        // Final value-scrub of the whole model-facing string: any secret header value or URL userinfo that leaked into
        // the response (an echo/redirect header, an error body) is masked before the model ever sees it.
        return redactor.Redact(builder.ToString());
    }

    private static void AppendSafeHeaders(StringBuilder builder, HttpHeaders headers)
    {
        foreach (var header in headers.Where(static header => !StrippedResponseHeaders.Contains(header.Key)))
        {
            builder.Append(CultureInfo.InvariantCulture, $"{header.Key}: {string.Join(", ", header.Value)}");
            builder.Append('\n');
        }
    }

    private static async Task<string> ReadCappedBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[MaxResponseBodyBytes];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, total);
            return total >= buffer.Length ? text + "\n…[response truncated]" : text;
        }
    }
}
