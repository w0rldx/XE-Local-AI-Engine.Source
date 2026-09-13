namespace XE_Local_AI_Engine.Tests.ExternalApps.Catalog;

using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog.Implementation;
using XE_Local_AI_Engine.Client.Testing.ExternalApps;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ApplicationCatalogProvider" />: bundled-only when no (or an unacceptable) refresh URL is configured,
///     a successful remote fetch replaces the served catalog and persists it, and — critically — a failed
///     fetch/validation falls back to the persisted last-good copy, else the bundled seed, WITHOUT throwing and
///     WITHOUT losing the reason. The cache store is the real file-backed
///     <see cref="ExternalAppCatalogCacheStore" /> over a temp data directory, not a stub, so the file round-trip and
///     its source-URL binding are covered here rather than in a separate suite.
/// </summary>
public sealed class ApplicationCatalogProviderTests : IDisposable
{
    private const string RemoteUrl = "https://catalog.test/applications.json";
    private const string OtherRemoteUrl = "https://other-catalog.test/applications.json";
    private const string RemoteGeneratedAtUtc = "2030-01-01T00:00:00Z";

    private readonly List<IDisposable> _disposables = [];
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "xe-external-apps-catalog-tests", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    /// <summary>
    ///     The test-only sample manifest served as a synthetic remote body. It has to declare an application: the
    ///     bundled seed ships none, so a remote document copied from it would be indistinguishable from bundled.
    /// </summary>
    private static string RemoteJson { get; } = SampleCatalogManifest.WithGeneratedAtUtc(RemoteGeneratedAtUtc);

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    [Test]
    public async Task GetCatalogAsync_WhenNoRefreshUrlConfigured_NeverFetchesAndServesBundled()
    {
        var handler = new CountingStubHandler(HttpStatusCode.OK, RemoteJson);
        var provider = BuildProvider(handler, refreshUrl: null, out _);

        var snapshot = await provider.GetCatalogAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.Bundled, snapshot.Source);
        AssertEx.Equal(expected: 0, handler.CallCount);
        AssertEx.Empty(snapshot.Document.Applications, "the bundled seed ships no application; serving the remote body here would show one.");
    }

    [Test]
    public async Task GetCatalogAsync_WhenRefreshSucceeds_ServesRemoteAndPersistsIt()
    {
        var handler = new CountingStubHandler(HttpStatusCode.OK, RemoteJson);
        var provider = BuildProvider(handler, RemoteUrl, out _);

        var snapshot = await provider.GetCatalogAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.Remote, snapshot.Source);
        AssertEx.Equal(RemoteGeneratedAtUtc, snapshot.Document.GeneratedAtUtc);
        AssertEx.Equal(RemoteUrl, snapshot.SourceUrl);

        var persisted = await File.ReadAllTextAsync(CachePath, CancellationToken.None);
        AssertEx.Contains(persisted, RemoteUrl);
        AssertEx.Contains(persisted, RemoteGeneratedAtUtc);
    }

    [Test]
    public async Task GetCatalogAsync_WhenRemoteFailsValidation_FallsBackToLastGood()
    {
        await PersistCacheAsync(RemoteJson, RemoteUrl, etag: null);
        var handler = new CountingStubHandler(HttpStatusCode.OK, """{ "schemaVersion": 99 }""");
        var provider = BuildProvider(handler, RemoteUrl, out _);

        var snapshot = await provider.GetCatalogAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.RemoteLastGood, snapshot.Source);
        AssertEx.Equal(RemoteGeneratedAtUtc, snapshot.Document.GeneratedAtUtc);
        AssertEx.NotNull(snapshot.LastRefreshFailure);
        AssertEx.Contains(snapshot.LastRefreshFailure, "failed validation");
    }

    [Test]
    public async Task GetCatalogAsync_WhenRemoteFailsValidationAndNoLastGood_ServesBundled()
    {
        var handler = new CountingStubHandler(HttpStatusCode.OK, """{ "schemaVersion": 99 }""");
        var provider = BuildProvider(handler, RemoteUrl, out _);

        var snapshot = await provider.GetCatalogAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.Bundled, snapshot.Source);
        AssertEx.NotEqual(RemoteGeneratedAtUtc, snapshot.Document.GeneratedAtUtc);
        AssertEx.Contains(snapshot.LastRefreshFailure, "failed validation");
    }

    [Test]
    public async Task GetCatalogAsync_WhenAlreadyServingRemote_DoesNotRegressToBundledOnATransientFailure()
    {
        var handler = new CountingStubHandler(HttpStatusCode.OK, RemoteJson);
        var provider = BuildProvider(handler, RemoteUrl, out _);
        var first = await provider.RefreshAsync(CancellationToken.None);
        AssertEx.Equal(ExternalAppCatalogSource.Remote, first.Snapshot.Source);

        handler.Reconfigure(HttpStatusCode.ServiceUnavailable, body: null);
        var second = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.Remote, second.Snapshot.Source);
        AssertEx.Equal(RemoteGeneratedAtUtc, second.Snapshot.Document.GeneratedAtUtc);
        AssertEx.Equal(expected: 2, handler.CallCount);
        AssertEx.Contains(second.FailureMessage, "HTTP 503");
    }

    [Test]
    public async Task GetCatalogAsync_WithinTtl_DoesNotFetchTwice()
    {
        var handler = new CountingStubHandler(HttpStatusCode.OK, RemoteJson);
        var provider = BuildProvider(handler, RemoteUrl, out var timeProvider);

        await provider.GetCatalogAsync(CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromHours(1));
        await provider.GetCatalogAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, handler.CallCount);
    }

    [Test]
    public async Task GetCatalogAsync_AfterTheTtlExpires_FetchesAgain()
    {
        var handler = new CountingStubHandler(HttpStatusCode.OK, RemoteJson);
        var provider = BuildProvider(handler, RemoteUrl, out var timeProvider);

        await provider.GetCatalogAsync(CancellationToken.None);
        timeProvider.Advance(ExternalAppCatalogOptions.DefaultRefreshTtl + TimeSpan.FromMinutes(1));
        await provider.GetCatalogAsync(CancellationToken.None);

        AssertEx.Equal(expected: 2, handler.CallCount);
    }

    [Test]
    public async Task RefreshAsync_WithinTtl_AlwaysFetches()
    {
        // The operator-forced refresh must never be a silent TTL no-op, unlike GetCatalogAsync's gated fast path.
        var handler = new CountingStubHandler(HttpStatusCode.OK, RemoteJson);
        var provider = BuildProvider(handler, RemoteUrl, out _);

        await provider.RefreshAsync(CancellationToken.None);
        await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(expected: 2, handler.CallCount);
    }

    [Test]
    public async Task GetCatalogAsync_ConcurrentCallers_FetchOnce()
    {
        // Every caller is held inside the handler until all of them have queued behind the refresh gate, so the single
        // request count is proof of single-flight rather than of the calls happening to run one after another. Only the
        // request count is asserted: a caller that arrives after _lastAttemptUtc is stamped but before the in-flight
        // fetch returns is served the current (still bundled) snapshot by the documented best-effort TTL peek, and
        // that is the intended behaviour — never a second fetch.
        var handler = new GatedStubHandler(RemoteJson);
        var provider = BuildProvider(handler, RemoteUrl, out _);

        var readers = Enumerable.Range(start: 0, count: 8)
                                .Select(_ => Task.Run(() => provider.GetCatalogAsync(CancellationToken.None)))
                                .ToArray();

        await handler.FirstRequestStarted;
        await AssertEx.SettleAsync();
        handler.Release();
        var snapshots = await Task.WhenAll(readers);

        AssertEx.Equal(expected: 1, handler.CallCount);
        AssertEx.Contains(snapshots, snapshot => snapshot.Source == ExternalAppCatalogSource.Remote);
        AssertEx.Equal(ExternalAppCatalogSource.Remote, (await provider.GetCatalogAsync(CancellationToken.None)).Source);
    }

    [Test]
    public async Task RefreshAsync_WhenRemoteReturnsNotModified_KeepsTheCurrentSnapshot()
    {
        var handler = new CountingStubHandler(HttpStatusCode.OK, RemoteJson);
        var provider = BuildProvider(handler, RemoteUrl, out _);
        await provider.RefreshAsync(CancellationToken.None);

        handler.Reconfigure(HttpStatusCode.NotModified, body: null);
        var result = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.Remote, result.Snapshot.Source);
        AssertEx.Equal(RemoteGeneratedAtUtc, result.Snapshot.Document.GeneratedAtUtc);
        AssertEx.Null(result.FailureMessage);
    }

    [Test]
    public async Task RefreshAsync_WhenTheCachedCopyIsStillCurrent_PromotesItOnANotModified()
    {
        // A 304 answered on a bundled-only start means the persisted copy IS the current catalog; serving bundled
        // there would ignore what the origin just confirmed.
        await PersistCacheAsync(RemoteJson, RemoteUrl, "\"v1\"");
        var handler = new CountingStubHandler(HttpStatusCode.NotModified, body: null);
        var provider = BuildProvider(handler, RemoteUrl, out _);

        var result = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.RemoteLastGood, result.Snapshot.Source);
        AssertEx.Equal(RemoteGeneratedAtUtc, result.Snapshot.Document.GeneratedAtUtc);
        AssertEx.Null(result.FailureMessage);
    }

    [Test]
    public async Task RefreshAsync_WhenResponseExceedsMaxDocumentBytes_FallsBackWithoutBufferingIt()
    {
        var handler = new CountingStubHandler(HttpStatusCode.OK, new string('x', count: 4096));
        var provider = BuildProvider(handler, RemoteUrl, out _, maxDocumentBytes: 64);

        var result = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.Bundled, result.Snapshot.Source);
        AssertEx.Contains(result.FailureMessage, "64 byte limit");
        AssertEx.Contains(result.FailureMessage, "declared");
    }

    [Test]
    public async Task RefreshAsync_WhenAnUndeclaredLengthExceedsMaxDocumentBytes_FallsBackAtTheReadCap()
    {
        // Content-Length is whatever the origin says; the length-capped read is the enforcement that does not trust it.
        var handler = new CountingStubHandler(HttpStatusCode.OK, body: null)
        {
            UndeclaredLengthBody = new string('x', count: 4096)
        };
        var provider = BuildProvider(handler, RemoteUrl, out _, maxDocumentBytes: 64);

        var result = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.Bundled, result.Snapshot.Source);
        AssertEx.Contains(result.FailureMessage, "exceeded the 64 byte limit");
    }

    [Test]
    public async Task RefreshAsync_WhenTransportThrows_DoesNotPropagate()
    {
        var handler = new CountingStubHandler(HttpStatusCode.OK, body: null)
        {
            Throw = () => new HttpRequestException("connection refused")
        };
        var provider = BuildProvider(handler, RemoteUrl, out _);

        var result = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.Bundled, result.Snapshot.Source);
        AssertEx.Contains(result.FailureMessage, "could not be completed");
    }

    [Test]
    public async Task RefreshAsync_WhenFetchFails_ReturnsTheFailureMessageAndCarriesItOnTheSnapshot()
    {
        var handler = new CountingStubHandler(HttpStatusCode.ServiceUnavailable, body: null);
        var provider = BuildProvider(handler, RemoteUrl, out _);

        var failed = await provider.RefreshAsync(CancellationToken.None);
        AssertEx.NotNull(failed.FailureMessage);
        AssertEx.Contains(failed.FailureMessage, "HTTP 503");

        var afterFailure = await provider.GetCatalogAsync(CancellationToken.None);
        AssertEx.Equal(failed.FailureMessage!, afterFailure.LastRefreshFailure);

        handler.Reconfigure(HttpStatusCode.OK, RemoteJson);
        var succeeded = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Null(succeeded.FailureMessage);
        AssertEx.Null(succeeded.Snapshot.LastRefreshFailure);
    }

    [Test]
    public async Task RefreshAsync_WhenRefreshUrlIsPlainHttpToAPublicHost_NeverFetches()
    {
        var handler = new CountingStubHandler(HttpStatusCode.OK, RemoteJson);
        var provider = BuildProvider(handler, "http://catalog.test/applications.json", out _);

        var result = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, handler.CallCount);
        AssertEx.Equal(ExternalAppCatalogSource.Bundled, result.Snapshot.Source);
        AssertEx.Null(result.FailureMessage);
    }

    [Test]
    public async Task RefreshAsync_WhenRefreshUrlIsHttpToLoopback_Fetches()
    {
        var handler = new CountingStubHandler(HttpStatusCode.OK, RemoteJson);
        var provider = BuildProvider(handler, "http://127.0.0.1:8099/applications.json", out _);

        var result = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, handler.CallCount);
        AssertEx.Equal(ExternalAppCatalogSource.Remote, result.Snapshot.Source);
    }

    [Test]
    public async Task RefreshAsync_WhenRemoteRedirects_TreatsTheRedirectAsAFailureAndNeverFollowsIt()
    {
        // The other half of this rule is the DI module's AllowAutoRedirect = false; here the provider's own treatment
        // of a 3xx is what is under test: one request, no second hop, a fallback carrying the status.
        var handler = new CountingStubHandler(HttpStatusCode.Found, body: null)
        {
            LocationHeader = "http://elsewhere.test/applications.json"
        };
        var provider = BuildProvider(handler, RemoteUrl, out _);

        var result = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, handler.CallCount);
        AssertEx.Equal(ExternalAppCatalogSource.Bundled, result.Snapshot.Source);
        AssertEx.Contains(result.FailureMessage, "HTTP 302");
    }

    [Test]
    public async Task FallbackToLastGood_WhenTheCachedSourceUrlDiffersFromTheConfiguredOne_IgnoresTheCache()
    {
        await PersistCacheAsync(RemoteJson, OtherRemoteUrl, etag: null);
        var handler = new CountingStubHandler(HttpStatusCode.ServiceUnavailable, body: null);
        var provider = BuildProvider(handler, RemoteUrl, out _);

        var result = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.Bundled, result.Snapshot.Source);
        AssertEx.NotEqual(RemoteGeneratedAtUtc, result.Snapshot.Document.GeneratedAtUtc);

        // The next success overwrites the foreign copy rather than leaving it to be reconsidered.
        handler.Reconfigure(HttpStatusCode.OK, RemoteJson);
        await provider.RefreshAsync(CancellationToken.None);
        AssertEx.Contains(await File.ReadAllTextAsync(CachePath, CancellationToken.None), RemoteUrl);
    }

    [Test]
    public async Task FallbackToLastGood_WhenTheCachedSourceUrlMatches_ServesTheCache()
    {
        await PersistCacheAsync(RemoteJson, RemoteUrl, etag: null);
        var handler = new CountingStubHandler(HttpStatusCode.ServiceUnavailable, body: null);
        var provider = BuildProvider(handler, RemoteUrl, out _);

        var result = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.RemoteLastGood, result.Snapshot.Source);
        AssertEx.Equal(RemoteGeneratedAtUtc, result.Snapshot.Document.GeneratedAtUtc);
    }

    [Test]
    public async Task RefreshAsync_WhenTheCachedSourceUrlDiffers_SendsNoIfNoneMatchHeader()
    {
        await PersistCacheAsync(RemoteJson, OtherRemoteUrl, "\"v1\"");
        var handler = new CountingStubHandler(HttpStatusCode.OK, RemoteJson);
        var provider = BuildProvider(handler, RemoteUrl, out _);

        await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Null(handler.LastIfNoneMatch);
    }

    [Test]
    public async Task RefreshAsync_WhenTheCachedSourceUrlMatches_SendsItsETag()
    {
        await PersistCacheAsync(RemoteJson, RemoteUrl, "\"v1\"");
        var handler = new CountingStubHandler(HttpStatusCode.OK, RemoteJson);
        var provider = BuildProvider(handler, RemoteUrl, out _);

        await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal("\"v1\"", handler.LastIfNoneMatch);
    }

    [Test]
    public async Task GetApplicationAsync_ReturnsTheDeclaredManifestAndNullForAnUnknownId()
    {
        // Served from the remote body, not the bundled seed: the seed declares no application to look up.
        var handler = new CountingStubHandler(HttpStatusCode.OK, RemoteJson);
        var provider = BuildProvider(handler, RemoteUrl, out _);

        var declared = await provider.GetApplicationAsync(SampleCatalogManifest.ApplicationId, CancellationToken.None);
        var unknown = await provider.GetApplicationAsync("not-in-the-catalog", CancellationToken.None);

        AssertEx.Equal(SampleCatalogManifest.ApplicationId, AssertEx.NotNull(declared).Id);
        AssertEx.Null(unknown);
    }

    [Test]
    public async Task GetCatalogAsync_WhenTheCallerCancelsTheFetch_DoesNotPoisonTheRefreshTtl()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new CountingStubHandler(HttpStatusCode.OK, RemoteJson)
        {
            Throw = () =>
            {
                cancellation.Cancel();
                return new OperationCanceledException(cancellation.Token);
            }
        };
        var provider = BuildProvider(handler, RemoteUrl, out _);

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => provider.GetCatalogAsync(cancellation.Token));

        // No time has passed. A cancelled fetch that left the attempt stamp advanced would make this read short-circuit
        // on the TTL peek and serve the bundled seed for a whole RefreshTtl over a fetch that never happened.
        handler.Throw = null;
        var snapshot = await provider.GetCatalogAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.Remote, snapshot.Source);
        AssertEx.Equal(expected: 2, handler.CallCount);
    }

    [Test]
    public async Task GetCatalogAsync_WhenTheCacheFileCannotBeRead_DegradesToBundled()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            // Skip.Test throws; the return is what tells the platform-compatibility analyser the code below is POSIX-only.
            Skip.Test("File mode 0000 is a POSIX permission; a Windows ACL does not reproduce it.");
            return;
        }

        await PersistCacheAsync(RemoteJson, RemoteUrl, etag: null);
        File.SetUnixFileMode(CachePath, UnixFileMode.None);
        if (CanReadFile(CachePath))
        {
            Skip.Test("This process reads a 0000 file anyway (running as root), so the UnauthorizedAccessException path is unreachable.");
        }

        var handler = new CountingStubHandler(HttpStatusCode.InternalServerError, body: null);
        var provider = BuildProvider(handler, RemoteUrl, out _);

        var snapshot = await provider.GetCatalogAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.Bundled, snapshot.Source);
        AssertEx.Contains(snapshot.LastRefreshFailure, "HTTP 500");
    }

    [Test]
    public async Task GetCatalogAsync_WhenTheBodyCarriesAUtf8Bom_StillServesIt()
    {
        var handler = new CountingStubHandler(HttpStatusCode.OK, body: null)
        {
            RawBody = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(RemoteJson)]
        };
        var provider = BuildProvider(handler, RemoteUrl, out _);

        var snapshot = await provider.GetCatalogAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.Remote, snapshot.Source);
        AssertEx.Equal(RemoteGeneratedAtUtc, snapshot.Document.GeneratedAtUtc);
    }

    [Test]
    public async Task GetCatalogAsync_WhenTheBodyIsNotValidUtf8_FailsInsteadOfRepairingIt()
    {
        // 0xFF 0xFE can never start a UTF-8 sequence. A substituting decoder would turn it into U+FFFD and hand the
        // provider a body the catalog server never served.
        var handler = new CountingStubHandler(HttpStatusCode.OK, body: null)
        {
            RawBody = [0xFF, 0xFE, 0x7B, 0x7D]
        };
        var provider = BuildProvider(handler, RemoteUrl, out _);

        var snapshot = await provider.GetCatalogAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.Bundled, snapshot.Source);
        AssertEx.Contains(snapshot.LastRefreshFailure, "not valid UTF-8");
    }

    [Test]
    public async Task RefreshAsync_WhenTheCachedCopyDoesNotValidate_SendsNoIfNoneMatchHeader()
    {
        // An ETag promises the node already holds the representation it names. Sent for a body the node cannot serve,
        // every later 304 would be answered by falling back to bundled — and the token would never be retired.
        await PersistCacheAsync("""{ "schemaVersion": 99 }""", RemoteUrl, "\"v1\"");
        var handler = new CountingStubHandler(HttpStatusCode.OK, RemoteJson);
        var provider = BuildProvider(handler, RemoteUrl, out _);

        var snapshot = await provider.GetCatalogAsync(CancellationToken.None);

        AssertEx.Null(handler.LastIfNoneMatch);
        AssertEx.Equal(ExternalAppCatalogSource.Remote, snapshot.Source);
    }

    [Test]
    public async Task RefreshAsync_WhenANotModifiedArrivesWithNoUsableCachedCopy_RefetchesUnconditionally()
    {
        var handler = new CountingStubHandler(HttpStatusCode.OK, RemoteJson)
        {
            FirstCallStatus = HttpStatusCode.NotModified
        };
        var provider = BuildProvider(handler, RemoteUrl, out _);

        var result = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.Remote, result.Snapshot.Source);
        AssertEx.Equal(RemoteGeneratedAtUtc, result.Snapshot.Document.GeneratedAtUtc);
        AssertEx.Equal(expected: 2, handler.CallCount);
        AssertEx.Null(handler.LastIfNoneMatch);
        AssertEx.Null(result.FailureMessage);
    }

    [Test]
    public async Task RefreshAsync_WhenTheOriginKeepsAnsweringNotModified_ReportsAFailureInsteadOfSuccess()
    {
        var handler = new CountingStubHandler(HttpStatusCode.NotModified, body: null);
        var provider = BuildProvider(handler, RemoteUrl, out _);

        var result = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.Bundled, result.Snapshot.Source);
        AssertEx.Equal(expected: 2, handler.CallCount);
        AssertEx.Contains(result.FailureMessage, "304");
    }

    [Test]
    public async Task GetCatalogAsync_WhenAFetchFails_RetriesAfterTheFailureBackoffAndThenHonoursTheFullTtl()
    {
        var handler = new CountingStubHandler(HttpStatusCode.ServiceUnavailable, body: null);
        var provider = BuildProvider(handler, RemoteUrl, out var timeProvider);

        var failed = await provider.GetCatalogAsync(CancellationToken.None);
        AssertEx.Equal(ExternalAppCatalogSource.Bundled, failed.Source);

        // A dead origin must not be hit on every read...
        _ = await provider.GetCatalogAsync(CancellationToken.None);
        AssertEx.Equal(expected: 1, handler.CallCount);

        // ...but recovery must not wait a whole RefreshTtl either, which is what the outer TTL peek used to impose.
        timeProvider.Advance(ExternalAppCatalogOptions.DefaultFailureRetryInterval + TimeSpan.FromSeconds(seconds: 1));
        handler.Reconfigure(HttpStatusCode.OK, RemoteJson);
        var recovered = await provider.GetCatalogAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.Remote, recovered.Source);
        AssertEx.Equal(expected: 2, handler.CallCount);

        // Once a snapshot is remote the short backoff is gone and the ordinary TTL governs again.
        timeProvider.Advance(ExternalAppCatalogOptions.DefaultFailureRetryInterval + TimeSpan.FromSeconds(seconds: 1));
        _ = await provider.GetCatalogAsync(CancellationToken.None);

        AssertEx.Equal(expected: 2, handler.CallCount);
    }

    [Test]
    public async Task RefreshAsync_WhenTheBodyStreamThrowsIoException_DegradesInsteadOfPropagating()
    {
        // With ResponseHeadersRead the body is still on the wire here, so a connection that dies mid-read surfaces as
        // IOException, not HttpRequestException — and used to escape the refresh instead of falling back.
        var handler = new CountingStubHandler(HttpStatusCode.OK, body: null)
        {
            FailBodyRead = true
        };
        var provider = BuildProvider(handler, RemoteUrl, out _);

        var result = await provider.RefreshAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppCatalogSource.Bundled, result.Snapshot.Source);
        AssertEx.Contains(result.FailureMessage, "could not be completed");
    }

    private static bool CanReadFile(string path)
    {
        try
        {
            using var probe = File.OpenRead(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string CachePath => Path.Combine(_dataRoot, "external-apps", "catalog-remote-cache.json");

    private async Task PersistCacheAsync(string rawJson, string sourceUrl, string? etag)
    {
        var store = new ExternalAppCatalogCacheStore(new FakeNodeDataDirectory(_dataRoot), NullLogger<ExternalAppCatalogCacheStore>.Instance);
        using (store)
        {
            await store.SaveAsync(new StoredExternalAppCatalogCache(rawJson, DateTimeOffset.UnixEpoch, sourceUrl, etag), CancellationToken.None);
        }
    }

    private ApplicationCatalogProvider BuildProvider(HttpMessageHandler handler,
        string? refreshUrl,
        out AdvanceableTimeProvider timeProvider,
        int maxDocumentBytes = ExternalAppCatalogOptions.DefaultMaxDocumentBytes)
    {
        _disposables.Add(handler);
        var httpClient = new HttpClient(handler);
        _disposables.Add(httpClient);

        var cacheStore = new ExternalAppCatalogCacheStore(new FakeNodeDataDirectory(_dataRoot), NullLogger<ExternalAppCatalogCacheStore>.Instance);
        _disposables.Add(cacheStore);

        var options = Options.Create(new ExternalAppCatalogOptions
        {
            RefreshUrl = refreshUrl,
            RefreshTtl = ExternalAppCatalogOptions.DefaultRefreshTtl,
            FetchTimeout = TimeSpan.FromSeconds(seconds: 5),
            FailureRetryInterval = ExternalAppCatalogOptions.DefaultFailureRetryInterval,
            MaxDocumentBytes = maxDocumentBytes
        });

        timeProvider = new AdvanceableTimeProvider();
        var provider = new ApplicationCatalogProvider(new FakeHttpClientFactory(httpClient),
            cacheStore,
            options,
            timeProvider,
            NullLogger<ApplicationCatalogProvider>.Instance);
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
        private string? _body = body;
        private HttpStatusCode _statusCode = statusCode;

        public int CallCount { get; private set; }

        public string? LastIfNoneMatch { get; private set; }

        public string? LocationHeader { get; init; }

        /// <summary>A body served without a <c>Content-Length</c>, so only the length-capped read can reject it.</summary>
        public string? UndeclaredLengthBody { get; init; }

        /// <summary>Settable, not init-only: one test throws on the first call and succeeds on the second.</summary>
        public Func<Exception>? Throw { get; set; }

        /// <summary>A body served as exact bytes, so a BOM or an invalid UTF-8 sequence survives to the provider.</summary>
        public byte[]? RawBody { get; init; }

        /// <summary>Answers the first call with no body, so a retry can differ from the attempt that provoked it.</summary>
        public HttpStatusCode? FirstCallStatus { get; init; }

        /// <summary>Serves a body whose stream fails partway through, the shape a connection dropped mid-body takes.</summary>
        public bool FailBodyRead { get; init; }

        public void Reconfigure(HttpStatusCode statusCode, string? body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            CallCount++;
            LastIfNoneMatch = request.Headers.TryGetValues("If-None-Match", out var values) ? values.FirstOrDefault() : null;

            if (Throw is not null)
            {
                throw Throw();
            }

            if (FirstCallStatus is { } firstCallStatus && CallCount == 1)
            {
                return Task.FromResult(new HttpResponseMessage(firstCallStatus));
            }

            if (FailBodyRead)
            {
                return Task.FromResult(new HttpResponseMessage(_statusCode)
                {
                    Content = new FailingReadContent()
                });
            }

            var response = new HttpResponseMessage(_statusCode);
            if (_body is not null)
            {
                response.Content = new StringContent(_body);
            }
            else if (UndeclaredLengthBody is not null)
            {
                response.Content = new UndeclaredLengthContent(UndeclaredLengthBody);
            }
            else if (RawBody is not null)
            {
                response.Content = new ByteArrayContent(RawBody);
            }

            if (LocationHeader is not null)
            {
                response.Headers.Location = new Uri(LocationHeader);
            }

            return Task.FromResult(response);
        }
    }

    /// <summary>Holds every caller inside the handler until the test releases it, so a fetch stampede would be visible.</summary>
    private sealed class GatedStubHandler(string body) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _firstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task FirstRequestStarted => _firstRequest.Task;

        public void Release()
        {
            _ = _release.TrySetResult();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _callCount);
            _ = _firstRequest.TrySetResult();
            await _release.Task.ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            };
        }
    }

    /// <summary>Content whose read stream throws <see cref="IOException" />, as a connection reset mid-body does.</summary>
    private sealed class FailingReadContent : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return Task.FromResult<Stream>(new FailingReadStream());
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return Task.FromException(new IOException("the connection was reset mid-body"));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class FailingReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new IOException("the connection was reset mid-body");
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromException<int>(new IOException("the connection was reset mid-body"));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Content that refuses to declare its length, so <c>Content-Length</c> cannot be the thing that rejects it.</summary>
    private sealed class UndeclaredLengthContent(string body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ArgumentNullException.ThrowIfNull(stream);
            var bytes = Encoding.UTF8.GetBytes(body);
            return stream.WriteAsync(bytes, offset: 0, bytes.Length);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
