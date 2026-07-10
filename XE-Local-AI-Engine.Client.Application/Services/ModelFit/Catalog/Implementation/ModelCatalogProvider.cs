namespace XE_Local_AI_Engine.Client.Services.ModelFit.Catalog.Implementation;

using Microsoft.Extensions.Options;

/// <summary>
///     Default <see cref="IModelCatalogProvider" />: bundled at construction, optionally kept fresh from
///     <see cref="ModelCatalogOptions.RefreshUrl" /> (locked decision D1). Singleton — an in-memory
///     <see cref="_current" /> snapshot is served on every read; a refresh (TTL-triggered or forced) is serialized
///     through <see cref="_refreshGate" /> so concurrent readers never trigger a fetch stampede.
///     <para>
///         Fallback chain on a failed remote fetch/validation: keep serving an already-effective remote/last-good
///         snapshot unchanged (a transient failure must never regress a working cache); otherwise fall back to the
///         persisted last-good remote catalog; otherwise the bundled seed. A successful fetch replaces the in-memory
///         snapshot AND persists the raw JSON so a restart with the network down still serves the last-good remote
///         catalog rather than regressing to bundled.
///     </para>
/// </summary>
internal sealed class ModelCatalogProvider : IModelCatalogProvider, IDisposable
{
    private readonly IModelCatalogCacheStore _cacheStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ModelCatalogProvider> _logger;
    private readonly IOptions<ModelCatalogOptions> _options;
    private readonly SemaphoreSlim _refreshGate = new(initialCount: 1, maxCount: 1);
    private readonly TimeProvider _timeProvider;

    // Both fields are only ever mutated while holding _refreshGate; GetCatalogAsync's TTL check reads them without the
    // lock as a fast-path best-effort peek — a torn/stale read there only means an occasional extra refresh attempt
    // (harmless), never a correctness issue, and RefreshCoreAsync re-checks them under the lock before acting.
    private ModelCatalogSnapshot _current;
    private DateTimeOffset _lastAttemptUtc = DateTimeOffset.MinValue;

    public ModelCatalogProvider(IHttpClientFactory httpClientFactory,
        IModelCatalogCacheStore cacheStore,
        IOptions<ModelCatalogOptions> options,
        TimeProvider timeProvider,
        ILogger<ModelCatalogProvider> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _cacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _current = new ModelCatalogSnapshot(ModelCatalogBundledLoader.Load(_logger), ModelCatalogSource.Bundled, FetchedAtUtc: null, SourceUrl: null);
    }

    public void Dispose()
    {
        _refreshGate.Dispose();
    }

    public Task<ModelCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (string.IsNullOrWhiteSpace(options.RefreshUrl))
        {
            return Task.FromResult(_current);
        }

        var now = _timeProvider.GetUtcNow();
        return now - _lastAttemptUtc < options.RefreshTtl
            ? Task.FromResult(_current)
            : RefreshCoreAsync(options.RefreshUrl, options, now, cancellationToken);
    }

    public Task<ModelCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        return string.IsNullOrWhiteSpace(options.RefreshUrl)
            ? Task.FromResult(_current)
            : RefreshCoreAsync(options.RefreshUrl, options, _timeProvider.GetUtcNow(), cancellationToken);
    }

    private async Task<ModelCatalogSnapshot> RefreshCoreAsync(string refreshUrl, ModelCatalogOptions options, DateTimeOffset attemptAtUtc, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check under the lock: a concurrent caller may already have refreshed while this one waited.
            if (attemptAtUtc - _lastAttemptUtc < options.RefreshTtl && _current.Source != ModelCatalogSource.Bundled)
            {
                return _current;
            }

            _lastAttemptUtc = attemptAtUtc;

            var client = _httpClientFactory.CreateClient(ModelCatalogOptions.HttpClientName);
            using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            fetchCts.CancelAfter(options.FetchTimeout);

            string raw;
            try
            {
                raw = await client.GetStringAsync(refreshUrl, fetchCts.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when ((exception is HttpRequestException or TaskCanceledException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Remote model catalog fetch failed; falling back to last-good/bundled.");
                return await FallbackToLastGoodAsync(cancellationToken).ConfigureAwait(false);
            }

            var validation = ModelCatalogValidator.Validate(raw);
            if (!validation.IsValid)
            {
                _logger.LogWarning(
                    "Remote model catalog failed validation ({ErrorCount} error(s)); falling back to last-good/bundled. First error: {FirstError}",
                    validation.Errors.Count,
                    validation.Errors.Count > 0 ? validation.Errors[0] : "(none)");
                return await FallbackToLastGoodAsync(cancellationToken).ConfigureAwait(false);
            }

            var snapshot = new ModelCatalogSnapshot(validation.Document!, ModelCatalogSource.Remote, attemptAtUtc, refreshUrl);
            _current = snapshot;

            await _cacheStore.SaveAsync(new StoredModelCatalogCache(raw, attemptAtUtc, refreshUrl), cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Must be called while holding <see cref="_refreshGate" />.</summary>
    private async Task<ModelCatalogSnapshot> FallbackToLastGoodAsync(CancellationToken cancellationToken)
    {
        // An already-effective remote/last-good snapshot is kept as-is — a single transient failure must never
        // regress a working cache back to the (potentially much older) bundled seed.
        if (_current.Source != ModelCatalogSource.Bundled)
        {
            return _current;
        }

        var stored = await _cacheStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return _current;
        }

        var validation = ModelCatalogValidator.Validate(stored.RawJson);
        if (!validation.IsValid)
        {
            _logger.LogWarning("Persisted last-good model catalog failed validation; serving bundled instead.");
            return _current;
        }

        var snapshot = new ModelCatalogSnapshot(validation.Document!, ModelCatalogSource.RemoteLastGood, stored.FetchedAtUtc, stored.SourceUrl);
        _current = snapshot;
        return snapshot;
    }
}
