namespace XE_Local_AI_Engine.Client.Services.ModelFit.Catalog.Implementation;

using Microsoft.Extensions.Options;

/// <summary>
///     Default <see cref="IModelCatalogProvider" />: bundled at construction, optionally kept fresh from
///     <see cref="ModelCatalogOptions.RefreshUrl" />. Singleton, so every read is served from the in-memory
///     <see cref="_current" /> snapshot.
/// </summary>
/// <remarks>
///     A refresh, TTL-triggered or forced, is serialized through <see cref="_refreshGate" /> so concurrent readers
///     never trigger a fetch stampede. On a failed remote fetch or validation an already-effective remote/last-good
///     snapshot is kept unchanged (a transient failure must never regress a working cache), else the persisted
///     last-good remote catalog, else the bundled seed. A successful fetch replaces the snapshot AND persists the raw
///     JSON, so a restart with the network down still serves last-good remote.
/// </remarks>
internal sealed class ModelCatalogProvider : IModelCatalogProvider, IDisposable
{
    private readonly IModelCatalogCacheStore _cacheStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ModelCatalogProvider> _logger;
    private readonly IOptions<ModelCatalogOptions> _options;
    private readonly SemaphoreSlim _refreshGate = new(initialCount: 1, maxCount: 1);
    private readonly TimeProvider _timeProvider;

    // Both fields are mutated only while holding _refreshGate; GetCatalogAsync's TTL check peeks at them lock-free, so a
    // stale read costs at most one extra refresh attempt, and RefreshCoreAsync re-checks them under the lock before acting.
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

        _current = new ModelCatalogSnapshot
        {
            Document = ModelCatalogBundledLoader.Load(_logger),
            Source = ModelCatalogSource.Bundled,
            FetchedAtUtc = null,
            SourceUrl = null
        };
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
            : RefreshCoreAsync(options.RefreshUrl, options, now, force: false, cancellationToken);
    }

    public Task<ModelCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        // force: true — an operator-triggered refresh must always attempt a fetch, never a silent TTL no-op just
        // because a previous refresh (scheduled or another operator click) already succeeded within the TTL window.
        return string.IsNullOrWhiteSpace(options.RefreshUrl)
            ? Task.FromResult(_current)
            : RefreshCoreAsync(options.RefreshUrl, options, _timeProvider.GetUtcNow(), force: true, cancellationToken);
    }

    private async Task<ModelCatalogSnapshot> RefreshCoreAsync(string refreshUrl,
        ModelCatalogOptions options,
        DateTimeOffset attemptAtUtc,
        bool force,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            // Re-check under the lock: a concurrent TTL-triggered caller may already have refreshed while this one waited.
            // The debounce is skipped when force is set (RefreshAsync); an operator-triggered refresh always fetches.
            if (!force && attemptAtUtc - _lastAttemptUtc < options.RefreshTtl && _current.Source != ModelCatalogSource.Bundled)
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
                raw = await client.GetStringAsync(refreshUrl, fetchCts.Token);
            }
            catch (Exception exception) when ((exception is HttpRequestException or TaskCanceledException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Remote model catalog fetch failed; falling back to last-good/bundled.");
                return await FallbackToLastGoodAsync(cancellationToken);
            }

            var validation = ModelCatalogValidator.Validate(raw);
            if (!validation.IsValid)
            {
                _logger.LogWarning("Remote model catalog failed validation ({ErrorCount} error(s)); falling back to last-good/bundled. First error: {FirstError}",
                    validation.Errors.Count,
                    validation.Errors.Count > 0 ? validation.Errors[0] : "(none)");
                return await FallbackToLastGoodAsync(cancellationToken);
            }

            var snapshot = new ModelCatalogSnapshot
            {
                Document = validation.Document!,
                Source = ModelCatalogSource.Remote,
                FetchedAtUtc = attemptAtUtc,
                SourceUrl = refreshUrl
            };
            _current = snapshot;

            await _cacheStore.SaveAsync(new StoredModelCatalogCache(raw, attemptAtUtc, refreshUrl), cancellationToken);
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

        var stored = await _cacheStore.LoadAsync(cancellationToken);
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

        var snapshot = new ModelCatalogSnapshot
        {
            Document = validation.Document!,
            Source = ModelCatalogSource.RemoteLastGood,
            FetchedAtUtc = stored.FetchedAtUtc,
            SourceUrl = stored.SourceUrl
        };
        _current = snapshot;
        return snapshot;
    }
}
