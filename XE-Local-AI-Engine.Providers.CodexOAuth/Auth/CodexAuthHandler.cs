namespace XE_Local_AI_Engine.Providers.CodexOAuth.Auth;

using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
///     <see cref="DelegatingHandler" /> that owns Codex auth on the SSE Responses path:
///     <list type="number">
///         <item>
///             Strips any <c>Authorization</c> the OpenAI SDK added from its dummy "unused" key, so it never
///             reaches the wire.
///         </item>
///         <item>
///             Injects the Codex header contract for the SSE path: real bearer <c>Authorization</c>,
///             <c>chatgpt-account-id</c>, <c>originator</c>, <c>User-Agent</c> — and NOT the WebSocket-only
///             <c>OpenAI-Beta</c>.
///         </item>
///         <item>
///             On <c>401</c>, performs a single-flight refresh (one gate; concurrent 401s await the same refresh
///             with double-checked expiry) and retries the request exactly once.
///         </item>
///     </list>
///     Never logs token values, authorization headers, or the dummy key.
/// </summary>
public sealed class CodexAuthHandler : DelegatingHandler
{
    private const string BearerScheme = "Bearer";
    private readonly ICodexAuthService _authService;
    private readonly ILogger<CodexAuthHandler> _logger;

    private readonly CodexOptions _options;

    // Single-flight refresh gate: concurrent 401s await one in-flight refresh, then re-check expiry.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly ICodexTokenStore _tokenStore;
    private CodexTokens? _cachedTokens;

    public CodexAuthHandler(IOptions<CodexOptions> options,
        ICodexTokenStore tokenStore,
        ICodexAuthService authService,
        ILogger<CodexAuthHandler> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokenStore);
        ArgumentNullException.ThrowIfNull(authService);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _tokenStore = tokenStore;
        _authService = authService;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tokens = await GetValidTokensAsync(false, cancellationToken).ConfigureAwait(false);
        ApplyHeaders(request, tokens);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            await LogFailureBodyAsync(response, cancellationToken).ConfigureAwait(false);
            return response;
        }

        // Single retry after a single-flight refresh. The original request was already sent — an
        // HttpRequestMessage cannot be resent (its content stream is consumed / it is marked used), so the retry
        // MUST go on a fresh CLONE of the request, not the original.
        response.Dispose();
        var refreshed = await GetValidTokensAsync(true, cancellationToken).ConfigureAwait(false);

        using var retryRequest = await CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
        ApplyHeaders(retryRequest, refreshed);
        var retryResponse = await base.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
        await LogFailureBodyAsync(retryResponse, cancellationToken).ConfigureAwait(false);
        return retryResponse;
    }

    /// <summary>
    ///     DIAGNOSTIC: on a non-success response from the Codex backend, log the error body so the node host log shows
    ///     the exact reason the call was rejected (e.g. <c>{"error":{"message","type","param":"model"}}</c>). The body
    ///     is buffered with <see cref="HttpContent.LoadIntoBufferAsync()" /> first, so reading it here does NOT consume
    ///     the content for the OpenAI SDK — the SDK still surfaces the same error to the caller. This is gated to
    ///     failure statuses ONLY: a success response carries the live SSE stream and must NOT be read here.
    ///     <para>
    ///         Token hygiene: the response body is the server's error JSON and never echoes request auth headers,
    ///         so logging it does not leak the bearer token / account id. Only the body and the status are logged — request
    ///         headers are never touched.
    ///     </para>
    /// </summary>
    private async Task LogFailureBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode || response.Content is null)
        {
            return;
        }

        string body;
        try
        {
            // Buffer so the SDK can still re-read the content when it builds its own ClientResultException.
            await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or IOException)
        {
            // Reading the diagnostic body must never break the request path; the SDK still surfaces the failure.
            _logger.LogWarning(exception, "Codex request failed with {StatusCode}; the error body could not be read for diagnostics.", (int)response.StatusCode);
            return;
        }

        _logger.LogWarning("Codex request to {RequestUri} failed with {StatusCode}. Error body: {ErrorBody}",
            response.RequestMessage?.RequestUri,
            (int)response.StatusCode,
            body);
    }

    /// <summary>
    ///     Builds a fresh, unsent copy of <paramref name="request" /> for the 401 retry: method, URI, version, options,
    ///     content (buffered so it can be re-read), and content headers. Request headers are re-applied by
    ///     <see cref="ApplyHeaders" /> after cloning. A sent <see cref="HttpRequestMessage" /> cannot be reused, so the
    ///     retry requires this clone.
    /// </summary>
    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };

        if (request.Content is not null)
        {
            // Buffer the original content so the bytes survive the first (consumed) send and can be re-read.
            var buffer = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var clonedContent = new ByteArrayContent(buffer);
            foreach (var header in request.Content.Headers)
            {
                clonedContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = clonedContent;
        }

        // Carry the request options (e.g. SDK transport keys) so the retry behaves identically.
        foreach (var option in request.Options)
        {
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        }

        return clone;
    }

    private async Task<CodexTokens> GetValidTokensAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        // Fast path: a cached, unexpired token and no forced refresh requested.
        var cached = _cachedTokens;
        if (!forceRefresh && cached is not null && !cached.IsExpired(_options.ExpirySkew, now))
        {
            return cached;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-checked: another caller may have refreshed while we waited on the gate.
            now = _timeProvider.GetUtcNow();
            cached = _cachedTokens ?? await _tokenStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (cached is null)
            {
                throw new CodexAuthException("No Codex session is available. Sign in via Codex login first.");
            }

            if (!forceRefresh && !cached.IsExpired(_options.ExpirySkew, now))
            {
                _cachedTokens = cached;
                return cached;
            }

            CodexTokens refreshed;
            try
            {
                // CodexAuthException already surfaces re-login intent; only wrap transport failures.
                refreshed = await _authService.RefreshAsync(cached, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                throw new CodexAuthException("Codex token refresh failed; re-login is required.", exception);
            }

            _cachedTokens = refreshed;
            return refreshed;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void ApplyHeaders(HttpRequestMessage request, CodexTokens tokens)
    {
        // Remove any Authorization the SDK injected from the dummy "unused" key before setting the real one.
        request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, tokens.AccessToken);

        SetHeader(request, CodexHeaders.AccountId, tokens.AccountId);
        SetHeader(request, CodexHeaders.Originator, _options.Originator);

        // LIVE-CORRECTNESS: a fresh per-request session-id (matches the working opencode reference client).
        SetHeader(request, CodexHeaders.SessionId, Guid.NewGuid().ToString());

        // User-Agent is a typed header; replace any existing SDK value.
        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation(CodexHeaders.UserAgent, _options.UserAgent);

        // Intentionally NOT setting OpenAI-Beta: responses_websockets — that header is WebSocket-only.
    }

    private static void SetHeader(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshGate.Dispose();
        }

        base.Dispose(disposing);
    }
}
