namespace XE_Local_AI_Engine.Tests.ModelFit.Catalog;

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;
using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ModelCatalogProvider" />: bundled-only when no refresh URL is configured, a successful remote fetch
///     replaces the served catalog and persists it, and — critically — a failed fetch/validation falls back to the
///     persisted last-good remote catalog, else the bundled seed, WITHOUT ever throwing out of the provider. TUnit
///     creates a fresh instance per test and disposes it afterward, so the per-test <see cref="HttpClient" />s created
///     by <see cref="BuildProvider" /> are tracked in <see cref="_disposables" /> and released in <see cref="Dispose" />.
/// </summary>
public sealed class ModelCatalogProviderTests : IDisposable
{
    private const string ValidRemoteJson =
        """
        {
          "schemaVersion": 1,
          "catalogVersion": "remote-1.0.0",
          "updatedAt": "2026-07-01",
          "models": [
            {
              "id": "remote-model",
              "family": "Remote",
              "displayName": "Remote Model",
              "publisher": "Remote Org",
              "ggufRepo": "org/remote-GGUF",
              "license": "mit",
              "tier": "S",
              "useCases": ["general"],
              "totalParamsB": 7.0,
              "activeParamsB": null,
              "moe": false,
              "contextLength": 8192,
              "minLlamaCppTag": "b9692",
              "releaseDate": "2026-01-01"
            }
          ]
        }
        """;

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    [Test]
    public async Task GetCatalogAsync_WhenNoRefreshUrlConfigured_NeverFetchesAndServesBundled()
    {
        var handler = new CountingStubHandler(HttpStatusCode.OK, ValidRemoteJson);
        var provider = BuildProvider(handler, refreshUrl: null, out _);

        var snapshot = await provider.GetCatalogAsync(CancellationToken.None);

        AssertEx.Equal(ModelCatalogSource.Bundled, snapshot.Source);
        AssertEx.Equal(expected: 0, handler.CallCount);
    }

    [Test]
    public async Task RefreshAsync_WhenRemoteFetchSucceeds_ReplacesSnapshotAndPersists()
    {
        var handler = new CountingStubHandler(HttpStatusCode.OK, ValidRemoteJson);
        var provider = BuildProvider(handler, refreshUrl: "https://example.test/catalog.json", out var cacheStore);

        var snapshot = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(ModelCatalogSource.Remote, snapshot.Source);
        AssertEx.Equal("remote-1.0.0", snapshot.Document.CatalogVersion);
        AssertEx.NotNull(cacheStore.Saved);
        AssertEx.Equal(ValidRemoteJson, cacheStore.Saved!.RawJson);
    }

    [Test]
    public async Task RefreshAsync_WhenFetchFailsAndNoLastGood_FallsBackToBundled()
    {
        var handler = new CountingStubHandler(HttpStatusCode.ServiceUnavailable, body: null);
        var provider = BuildProvider(handler, refreshUrl: "https://example.test/catalog.json", out _);

        var snapshot = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(ModelCatalogSource.Bundled, snapshot.Source);
    }

    [Test]
    public async Task RefreshAsync_WhenFetchFailsButLastGoodPersisted_FallsBackToLastGood()
    {
        var handler = new CountingStubHandler(HttpStatusCode.ServiceUnavailable, body: null);
        var cacheStore = new InMemoryCatalogCacheStore
        {
            Preloaded = new StoredModelCatalogCache(ValidRemoteJson, DateTimeOffset.UnixEpoch, "https://example.test/catalog.json")
        };
        var provider = BuildProvider(handler, "https://example.test/catalog.json", cacheStore);

        var snapshot = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(ModelCatalogSource.RemoteLastGood, snapshot.Source);
        AssertEx.Equal("remote-1.0.0", snapshot.Document.CatalogVersion);
    }

    [Test]
    public async Task RefreshAsync_WhenRemoteBodyFailsValidation_FallsBackWithoutThrowing()
    {
        var handler = new CountingStubHandler(HttpStatusCode.OK, """{ "schemaVersion": 99 }""");
        var provider = BuildProvider(handler, refreshUrl: "https://example.test/catalog.json", out _);

        var snapshot = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(ModelCatalogSource.Bundled, snapshot.Source);
    }

    [Test]
    public async Task GetCatalogAsync_WhenAlreadyRemoteAndNextFetchFails_KeepsServingWorkingCache()
    {
        // First call succeeds (Remote); the handler is then reconfigured to fail. A subsequent forced refresh must not
        // regress the already-working remote snapshot back to bundled — and it must have actually attempted the
        // second fetch (CallCount == 2), not silently no-op'd on the still-warm TTL.
        var handler = new CountingStubHandler(HttpStatusCode.OK, ValidRemoteJson);
        var provider = BuildProvider(handler, refreshUrl: "https://example.test/catalog.json", out _);
        var first = await provider.RefreshAsync(CancellationToken.None);
        AssertEx.Equal(ModelCatalogSource.Remote, first.Source);

        handler.Reconfigure(HttpStatusCode.ServiceUnavailable, body: null);
        var second = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(ModelCatalogSource.Remote, second.Source);
        AssertEx.Equal("remote-1.0.0", second.Document.CatalogVersion);
        AssertEx.Equal(expected: 2, handler.CallCount);
    }

    [Test]
    public async Task RefreshAsync_CalledTwiceInSuccession_AlwaysFetchesEvenWithinTtl()
    {
        // The operator-forced refresh must never be a silent TTL no-op: two RefreshAsync calls back-to-back — both
        // well within the 24h RefreshTtl — must each perform a real fetch (proven by CallCount == 2), unlike
        // GetCatalogAsync's TTL-gated fast path.
        var handler = new CountingStubHandler(HttpStatusCode.OK, ValidRemoteJson);
        var provider = BuildProvider(handler, refreshUrl: "https://example.test/catalog.json", out _);

        var first = await provider.RefreshAsync(CancellationToken.None);
        var second = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(ModelCatalogSource.Remote, first.Source);
        AssertEx.Equal(ModelCatalogSource.Remote, second.Source);
        AssertEx.Equal(expected: 2, handler.CallCount);
    }

    [Test]
    public async Task GetCatalogAsync_WithinTtlAfterRefresh_DoesNotRefetch()
    {
        // The TTL-gated fast path (GetCatalogAsync) must still debounce: a read immediately after a successful
        // refresh must NOT trigger another fetch. This is the behavior RefreshAsync's force-refresh must NOT disturb.
        var handler = new CountingStubHandler(HttpStatusCode.OK, ValidRemoteJson);
        var provider = BuildProvider(handler, refreshUrl: "https://example.test/catalog.json", out _);

        await provider.RefreshAsync(CancellationToken.None);
        await provider.GetCatalogAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, handler.CallCount);
    }

    private ModelCatalogProvider BuildProvider(HttpMessageHandler handler, string? refreshUrl, out InMemoryCatalogCacheStore cacheStore)
    {
        cacheStore = new InMemoryCatalogCacheStore();
        return BuildProvider(handler, refreshUrl, cacheStore);
    }

    private ModelCatalogProvider BuildProvider(HttpMessageHandler handler, string? refreshUrl, InMemoryCatalogCacheStore cacheStore)
    {
        _disposables.Add(handler);
        var httpClient = new HttpClient(handler);
        _disposables.Add(httpClient);

        var options = Options.Create(new ModelCatalogOptions
        {
            RefreshUrl = refreshUrl,
            RefreshTtl = TimeSpan.FromHours(24),
            FetchTimeout = TimeSpan.FromSeconds(5)
        });

        var provider = new ModelCatalogProvider(new FakeHttpClientFactory(httpClient),
            cacheStore,
            options,
            TimeProvider.System,
            NullLogger<ModelCatalogProvider>.Instance);
        _disposables.Add(provider);
        return provider;
    }

    private sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return client;
        }
    }

    private sealed class CountingStubHandler(HttpStatusCode statusCode, string? body) : HttpMessageHandler
    {
        private HttpStatusCode _statusCode = statusCode;
        private string? _body = body;

        public int CallCount { get; private set; }

        public void Reconfigure(HttpStatusCode statusCode, string? body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var response = new HttpResponseMessage(_statusCode);
            if (_body is not null)
            {
                response.Content = new StringContent(_body);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class InMemoryCatalogCacheStore : IModelCatalogCacheStore
    {
        public StoredModelCatalogCache? Preloaded { get; set; }

        public StoredModelCatalogCache? Saved { get; private set; }

        public Task<StoredModelCatalogCache?> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Saved ?? Preloaded);
        }

        public Task SaveAsync(StoredModelCatalogCache cache, CancellationToken cancellationToken = default)
        {
            Saved = cache;
            return Task.CompletedTask;
        }
    }
}
