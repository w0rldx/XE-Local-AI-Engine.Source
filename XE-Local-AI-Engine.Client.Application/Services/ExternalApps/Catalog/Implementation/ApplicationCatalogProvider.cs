namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog.Implementation;

using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;

/// <summary>Default <see cref="IApplicationCatalogProvider" />: bundled at construction, optionally kept fresh from <see cref="ExternalAppCatalogOptions.RefreshUrl" />.</summary>
/// <remarks>
///     Singleton: the in-memory <see cref="_current" /> snapshot is served on every read, and every refresh is
///     serialized through <see cref="_refreshGate" /> so concurrent readers never stampede the fetch. A failed fetch
///     or validation never regresses an already-effective snapshot, and no failure reaches the caller as an
///     exception. The whole chain is stated in <c>docs/wiki/23-external-apps.md</c> ("Architecture").
/// </remarks>
internal sealed class ApplicationCatalogProvider : IApplicationCatalogProvider, IDisposable
{
    /// <summary>
    ///     Decodes the fetched body. Throwing rather than substituting U+FFFD: a silently repaired body would be
    ///     hashed and persisted as if it were the document the catalog server served.
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The only hosts a plain-<c>http</c> refresh URL may name (the local catalog server used in live validation).</summary>
    private static readonly HashSet<string> LoopbackHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "127.0.0.1",
        "::1",
        "[::1]",
        "localhost"
    };

    private readonly IExternalAppCatalogCacheStore _cacheStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ApplicationCatalogProvider> _logger;
    private readonly IOptions<ExternalAppCatalogOptions> _options;

    /// <summary>The configured refresh URL once it passed the scheme/host rule, else <see langword="null" /> (bundled-only).</summary>
    private readonly string? _refreshUrl;

    private readonly SemaphoreSlim _refreshGate = new(initialCount: 1, maxCount: 1);
    private readonly TimeProvider _timeProvider;

    // Every field below is mutated only while holding _refreshGate. GetCatalogAsync's TTL check peeks at them without
    // the lock: a stale read costs at most one extra refresh attempt, and RefreshCoreAsync re-checks under the lock.
    private bool _cacheProbed;
    private ExternalAppCatalogSnapshot _current;
    private string? _lastETag;
    private DateTimeOffset _lastAttemptUtc = DateTimeOffset.MinValue;

    public ApplicationCatalogProvider(IHttpClientFactory httpClientFactory,
        IExternalAppCatalogCacheStore cacheStore,
        IOptions<ExternalAppCatalogOptions> options,
        TimeProvider timeProvider,
        ILogger<ApplicationCatalogProvider> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _cacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _refreshUrl = ResolveRefreshUrl(_options.Value.RefreshUrl, _logger);
        _current = new ExternalAppCatalogSnapshot(ExternalAppCatalogBundledLoader.Load(_logger),
            ExternalAppCatalogSource.Bundled,
            FetchedAtUtc: null,
            SourceUrl: null,
            LastRefreshFailure: null);
    }

    public void Dispose()
    {
        _refreshGate.Dispose();
    }

    public async Task<ExternalAppCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        if (_refreshUrl is null)
        {
            return _current;
        }

        var options = _options.Value;
        var now = _timeProvider.GetUtcNow();
        if (now - _lastAttemptUtc < RefreshDebounce(options, _current.Source))
        {
            return _current;
        }

        var result = await RefreshCoreAsync(_refreshUrl, options, now, force: false, cancellationToken);
        return result.Snapshot;
    }

    public async Task<ApplicationManifest?> GetApplicationAsync(string applicationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);

        var snapshot = await GetCatalogAsync(cancellationToken);
        return snapshot.Document.Applications.FirstOrDefault(manifest => string.Equals(manifest.Id, applicationId, StringComparison.Ordinal));
    }

    public Task<ExternalAppCatalogRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        // force: true — an operator-triggered refresh must always attempt a fetch, never a silent TTL no-op just
        // because a previous refresh already succeeded within the TTL window.
        return _refreshUrl is null
            ? Task.FromResult(new ExternalAppCatalogRefreshResult { Snapshot = _current, FailureMessage = null })
            : RefreshCoreAsync(_refreshUrl, _options.Value, _timeProvider.GetUtcNow(), force: true, cancellationToken);
    }

    /// <summary>
    ///     Returns <paramref name="configured" /> when it satisfies the scheme/host rule, else <see langword="null" />
    ///     after one Error naming the scheme and host but never the value. A rejected URL never fails startup: the node
    ///     comes up bundled-only and makes no outbound request.
    /// </summary>
    private static string? ResolveRefreshUrl(string? configured, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        if (Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                || (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && LoopbackHosts.Contains(uri.Host))))
        {
            return configured;
        }

        logger.LogError(
            "External Apps catalog refresh URL was rejected (scheme '{Scheme}', host '{Host}'): only https, or http to 127.0.0.1/::1/localhost, are accepted. Serving the bundled catalog only.",
            uri?.Scheme ?? "(unparseable)",
            uri?.Host ?? "(unparseable)");
        return null;
    }

    /// <summary>How long the last attempt suppresses the next one.</summary>
    /// <remarks>
    ///     A remote/last-good snapshot is on the ordinary <see cref="ExternalAppCatalogOptions.RefreshTtl" />. A
    ///     still-bundled one means the last attempt failed, and a whole TTL would suppress recovery for a day over
    ///     one failed fetch, so it waits only <see cref="ExternalAppCatalogOptions.FailureRetryInterval" /> — which
    ///     also keeps a dead origin from being hit on every catalog read. A non-positive interval means no backoff;
    ///     one longer than the TTL is capped there rather than making recovery slower than an ordinary refresh.
    /// </remarks>
    private static TimeSpan RefreshDebounce(ExternalAppCatalogOptions options, ExternalAppCatalogSource source)
    {
        if (source != ExternalAppCatalogSource.Bundled)
        {
            return options.RefreshTtl;
        }

        var backoff = options.FailureRetryInterval;
        return backoff <= TimeSpan.Zero || backoff > options.RefreshTtl ? TimeSpan.Zero : backoff;
    }

    /// <summary>Reads at most <paramref name="maxBytes" /> bytes, or <see langword="null" /> once the body exceeds it.</summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(start: 0, read), cancellationToken);
        }

        return buffer.ToArray();
    }

    private async Task<ExternalAppCatalogRefreshResult> RefreshCoreAsync(string refreshUrl,
        ExternalAppCatalogOptions options,
        DateTimeOffset attemptAtUtc,
        bool force,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            // Re-check under the lock: a concurrent TTL-triggered caller may already have refreshed while this one
            // waited. RefreshDebounce keeps a bundled-only start on a short backoff; force skips the debounce whole.
            if (!force && attemptAtUtc - _lastAttemptUtc < RefreshDebounce(options, _current.Source))
            {
                return new ExternalAppCatalogRefreshResult { Snapshot = _current, FailureMessage = null };
            }

            var previousAttemptUtc = _lastAttemptUtc;
            _lastAttemptUtc = attemptAtUtc;
            try
            {
                await ProbeCachedETagAsync(refreshUrl, cancellationToken);

                var outcome = await FetchAsync(refreshUrl, options, cancellationToken);
                if (outcome.NotModified)
                {
                    // The cached copy is still current; promote it if the in-memory snapshot is still the bundled seed.
                    var revalidated = await ApplyLastGoodAsync(refreshUrl, failureMessage: null, cancellationToken);
                    if (revalidated.Snapshot.Source != ExternalAppCatalogSource.Bundled)
                    {
                        return revalidated;
                    }

                    // The origin confirmed a representation this node cannot serve, and revalidating against a token
                    // whose body is gone only answers 304 again forever: drop the token and ask for the document.
                    _lastETag = null;
                    outcome = await FetchAsync(refreshUrl, options, cancellationToken);
                    if (outcome.NotModified)
                    {
                        return await ApplyLastGoodAsync(refreshUrl,
                                "Catalog refresh failed: the origin answered 304 to an unconditional request and no cached copy is usable.",
                                cancellationToken);
                    }
                }

                if (outcome.FailureMessage is not null)
                {
                    return await ApplyLastGoodAsync(refreshUrl, outcome.FailureMessage, cancellationToken);
                }

                var validation = ExternalAppCatalogValidator.Validate(outcome.Raw);
                if (!validation.IsValid)
                {
                    var firstError = validation.Errors.Count > 0 ? validation.Errors[0] : "(none)";
                    _logger.LogWarning("Remote External Apps catalog failed validation ({ErrorCount} error(s)); falling back to last-good/bundled. First error: {FirstError}",
                        validation.Errors.Count,
                        firstError);

                    var message = string.Create(CultureInfo.InvariantCulture,
                        $"Catalog refresh failed: the document failed validation ({validation.Errors.Count} errors, first: {firstError}).");
                    return await ApplyLastGoodAsync(refreshUrl, message, cancellationToken);
                }

                _current = new ExternalAppCatalogSnapshot(validation.Document!,
                    ExternalAppCatalogSource.Remote,
                    attemptAtUtc,
                    refreshUrl,
                    LastRefreshFailure: null);
                _lastETag = outcome.ETag;

                await _cacheStore.SaveAsync(new StoredExternalAppCatalogCache(outcome.Raw!, attemptAtUtc, refreshUrl, outcome.ETag), cancellationToken);
                return new ExternalAppCatalogRefreshResult { Snapshot = _current, FailureMessage = null };
            }
            catch (OperationCanceledException)
            {
                // The caller cancelled, so nothing was attempted and the stamp goes back: left advanced, it would make
                // GetCatalogAsync's TTL peek short-circuit every read for a whole RefreshTtl over a fetch never made.
                _lastAttemptUtc = previousAttemptUtc;
                throw;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    ///     Reads the persisted cache once per process to recover its revalidation token. Must be called while holding
    ///     <see cref="_refreshGate" />. An <c>ETag</c> belongs to one origin, so a copy fetched from a different URL
    ///     contributes none.
    /// </summary>
    private async Task ProbeCachedETagAsync(string refreshUrl, CancellationToken cancellationToken)
    {
        if (_cacheProbed)
        {
            return;
        }

        _cacheProbed = true;

        var stored = await _cacheStore.LoadAsync(cancellationToken);
        if (stored is null || !string.Equals(stored.SourceUrl, refreshUrl, StringComparison.Ordinal))
        {
            return;
        }

        // An ETag promises this node already holds the representation it names, and one sent for a cached body that
        // does not validate turns every later 304 into "keep serving bundled" permanently. Adopt it only if valid.
        if (ExternalAppCatalogValidator.Validate(stored.RawJson).IsValid)
        {
            _lastETag = stored.ETag;
            return;
        }

        _logger.LogWarning("Persisted External Apps catalog cache failed validation; its ETag will not be sent, so the next refresh fetches the document itself.");
    }

    private async Task<FetchOutcome> FetchAsync(string refreshUrl, ExternalAppCatalogOptions options, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(ExternalAppCatalogOptions.HttpClientName);
        using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        fetchCts.CancelAfter(options.FetchTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, refreshUrl);
            if (_lastETag is not null)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", _lastETag);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, fetchCts.Token);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new FetchOutcome { Raw = null, ETag = null, NotModified = true, FailureMessage = null };
            }

            if (!response.IsSuccessStatusCode)
            {
                // Redirects are OFF on this client (AddNodeExternalAppsCatalog), so a 3xx arrives as a plain non-success
                // status, never followed: the loopback-http allowance must not be bounceable to a public plain-http host.
                return FetchOutcome.Failed(string.Create(CultureInfo.InvariantCulture, $"Catalog refresh failed: HTTP {(int)response.StatusCode}."));
            }

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength > options.MaxDocumentBytes)
            {
                return FetchOutcome.Failed(string.Create(CultureInfo.InvariantCulture,
                    $"Catalog refresh failed: the response declared {declaredLength.Value} bytes, above the {options.MaxDocumentBytes} byte limit."));
            }

            var bytes = await ReadCappedAsync(response.Content, options.MaxDocumentBytes, fetchCts.Token);
            if (bytes is null)
            {
                return FetchOutcome.Failed(string.Create(CultureInfo.InvariantCulture, $"Catalog refresh failed: the response exceeded the {options.MaxDocumentBytes} byte limit."));
            }

            string raw;
            try
            {
                raw = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // Same fallback path an unparseable document takes: fail the fetch with a reason rather than let a
                // U+FFFD-repaired body through as if the server had served it.
                return FetchOutcome.Failed("Catalog refresh failed: the response was not valid UTF-8.");
            }

            // A BOM is legal UTF-8 but not legal JSON: leaving it in turns a valid catalog into an opaque parse error.
            if (raw.StartsWith('\uFEFF'))
            {
                raw = raw[1..];
            }

            return new FetchOutcome { Raw = raw, ETag = response.Headers.ETag?.ToString(), NotModified = false, FailureMessage = null };
        }
        // IOException covers the body: under ResponseHeadersRead the stream is still open, so a connection dying
        // mid-read surfaces as IOException (HttpIOException included) and must degrade like any transport failure.
        catch (Exception exception) when ((exception is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Remote External Apps catalog fetch failed; falling back to last-good/bundled.");

            return FetchOutcome.Failed(fetchCts.IsCancellationRequested
                ? string.Create(CultureInfo.InvariantCulture, $"Catalog refresh failed: the request timed out after {options.FetchTimeout.TotalSeconds}s.")
                : "Catalog refresh failed: the request could not be completed.");
        }
    }

    /// <summary>
    ///     Applies the fallback chain and stamps <paramref name="failureMessage" /> onto the served snapshot. Must be
    ///     called while holding <see cref="_refreshGate" />.
    /// </summary>
    private async Task<ExternalAppCatalogRefreshResult> ApplyLastGoodAsync(string refreshUrl, string? failureMessage, CancellationToken cancellationToken)
    {
        // An already-effective remote/last-good snapshot is kept as-is — a single transient failure must never regress
        // a working catalog back to the (potentially much older) bundled seed.
        if (_current.Source != ExternalAppCatalogSource.Bundled)
        {
            return Keep(failureMessage);
        }

        var stored = await _cacheStore.LoadAsync(cancellationToken);
        if (stored is null)
        {
            return Keep(failureMessage);
        }

        if (!string.Equals(stored.SourceUrl, refreshUrl, StringComparison.Ordinal))
        {
            // The cache belongs to a catalog source the operator has since moved away from; serving it would
            // resurrect the old source's document. It is overwritten by the next successful refresh.
            _logger.LogWarning("Persisted External Apps catalog cache was fetched from a different source URL; ignoring it.");
            return Keep(failureMessage);
        }

        var validation = ExternalAppCatalogValidator.Validate(stored.RawJson);
        if (!validation.IsValid)
        {
            _logger.LogWarning("Persisted last-good External Apps catalog failed validation; serving the bundled catalog instead.");
            return Keep(failureMessage);
        }

        _current = new ExternalAppCatalogSnapshot(validation.Document!,
            ExternalAppCatalogSource.RemoteLastGood,
            stored.FetchedAtUtc,
            stored.SourceUrl,
            failureMessage);
        return new ExternalAppCatalogRefreshResult { Snapshot = _current, FailureMessage = failureMessage };
    }

    private ExternalAppCatalogRefreshResult Keep(string? failureMessage)
    {
        _current = _current with
        {
            LastRefreshFailure = failureMessage
        };
        return new ExternalAppCatalogRefreshResult { Snapshot = _current, FailureMessage = failureMessage };
    }

    /// <summary>One fetch attempt's outcome: a body, a <c>304</c>, or a failure message describing the cause.</summary>
    private sealed record FetchOutcome
    {
        public required string? Raw { get; init; }

        public required string? ETag { get; init; }

        public required bool NotModified { get; init; }

        public required string? FailureMessage { get; init; }

        public static FetchOutcome Failed(string failureMessage)
        {
            return new FetchOutcome { Raw = null, ETag = null, NotModified = false, FailureMessage = failureMessage };
        }
    }
}
