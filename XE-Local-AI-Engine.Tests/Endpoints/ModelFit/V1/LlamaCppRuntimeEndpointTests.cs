namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Configuration;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint tests for the dynamic-runtime updater transport: the read-only runtime-status endpoint NEVER triggers a
///     binary download (it must not touch <see cref="ILlamaCppBinaryManager" />), and the update endpoint rejects a
///     malformed tag with a 400 before resolving any asset.
/// </summary>
public sealed class LlamaCppRuntimeEndpointTests
{
    private const string ApiPrefix = "/api/local/v1";

    [Test]
    public async Task RuntimeStatus_WhenAuthorized_ReturnsSnapshotWithoutTriggeringDownload()
    {
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        var updateState = new LlamaCppUpdateState();
        updateState.Store(new LlamaCppUpdateSnapshot(InstalledTag: "b9692",
            RecommendedTag: "b9700",
            UpstreamLatestTag: "b9777",
            UpdateAvailable: true,
            IsOffline: false,
            CheckedAtUtc: DateTimeOffset.UtcNow));

        await using var factory = CreateFactory(binaryManager, updateState);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/model-fit/llamacpp/runtime");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        // The snapshot drives updateAvailable + upstreamLatestTag; recommendedTag is sourced from the editable
        // node-settings accessor (the authoritative "recommended" value), not the snapshot.
        AssertEx.True(doc.RootElement.GetProperty("updateAvailable").GetBoolean(), "Seeded snapshot advertised an update.");
        AssertEx.Equal("b9777", doc.RootElement.GetProperty("upstreamLatestTag").GetString());

        // The read-only status endpoint must NEVER ensure/install a binary.
        await binaryManager.DidNotReceiveWithAnyArgs().EnsureBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>());
        await binaryManager.DidNotReceiveWithAnyArgs()
                           .InstallTagAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RuntimeStatus_RefreshWithinThrottleWindow_ServesCached_DoesNotCallCatalog()
    {
        // A snapshot stamped "now" is younger than the 60s minimum refresh interval, so ?refresh=true must serve the
        // cached snapshot and NOT re-hit the live catalog (protecting the 60/hr GitHub budget).
        var catalog = Substitute.For<ILlamaCppReleaseCatalog>();
        var updateState = new LlamaCppUpdateState();

        await using var factory = CreateFactory(Substitute.For<ILlamaCppBinaryManager>(), updateState, catalog);
        using var client = factory.CreateClient();

        // Stamped AFTER the host is up, and that ordering is the whole test. The endpoint reads the wall clock
        // directly (GetLlamaCppRuntimeEndpoint.IsStale uses DateTimeOffset.UtcNow, not an injected TimeProvider), so
        // "younger than the 60s refresh interval" is measured against real elapsed time — including however long
        // booting this host took. Stamping before CreateFactory made the window a race against startup: on Windows,
        // where the first request waits on 43 pending migrations against a Defender-scanned SQLite file, boot alone
        // exceeded sixty seconds. The snapshot was then genuinely stale, the throttle correctly permitted a refresh,
        // and the unconfigured catalog substitute returned a null Task — surfacing as a 500 that looked like an
        // endpoint defect rather than a fixture that had aged out its own precondition.
        updateState.Store(new LlamaCppUpdateSnapshot(InstalledTag: "b9692",
            RecommendedTag: "b9700",
            UpstreamLatestTag: "b9777",
            UpdateAvailable: true,
            IsOffline: false,
            CheckedAtUtc: DateTimeOffset.UtcNow));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/model-fit/llamacpp/runtime?refresh=true");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await catalog.DidNotReceiveWithAnyArgs().ResolveRecommendedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await catalog.DidNotReceiveWithAnyArgs().ResolveUpstreamLatestAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RuntimeStatus_RefreshWhenSnapshotStale_CallsCatalog()
    {
        // A snapshot older than the 60s interval is stale → ?refresh=true is honored and the catalog is queried.
        var catalog = Substitute.For<ILlamaCppReleaseCatalog>();
        catalog.ResolveRecommendedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(LlamaCppReleaseResult.ForTag("b9700"));
        catalog.ResolveUpstreamLatestAsync(Arg.Any<CancellationToken>())
               .Returns(LlamaCppReleaseResult.ForTag("b9777"));

        var updateState = new LlamaCppUpdateState();
        updateState.Store(new LlamaCppUpdateSnapshot(InstalledTag: "b9692",
            RecommendedTag: "b9700",
            UpstreamLatestTag: "b9777",
            UpdateAvailable: true,
            IsOffline: false,
            CheckedAtUtc: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5)));

        await using var factory = CreateFactory(Substitute.For<ILlamaCppBinaryManager>(), updateState, catalog);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/model-fit/llamacpp/runtime?refresh=true");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await catalog.ReceivedWithAnyArgs().ResolveRecommendedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateRuntime_WhenTagMalformed_ReturnsBadRequestWithoutInstalling()
    {
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        await using var factory = CreateFactory(binaryManager, new LlamaCppUpdateState());
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/model-fit/llamacpp/update")
        {
            Content = JsonContent.Create(new
            {
                tag = "../etc/passwd"
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await binaryManager.DidNotReceiveWithAnyArgs()
                           .InstallTagAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RuntimeStatus_WhenModelsRunning_ReportsRunningProcessCount()
    {
        // The runtime-status surface must reflect the supervisor's running-process count so the UI can gate the update.
        var updateState = new LlamaCppUpdateState();
        updateState.Store(new LlamaCppUpdateSnapshot(InstalledTag: "b9692",
            RecommendedTag: "b9700",
            UpstreamLatestTag: "b9777",
            UpdateAvailable: true,
            IsOffline: false,
            CheckedAtUtc: DateTimeOffset.UtcNow));
        var supervisor = new FakeProcessSupervisor(FakeProcessSupervisor.RunningChat("a"), FakeProcessSupervisor.RunningChat("b"));

        await using var factory = CreateFactory(Substitute.For<ILlamaCppBinaryManager>(), updateState, supervisor: supervisor);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/model-fit/llamacpp/runtime");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        AssertEx.Equal(expected: 2, doc.RootElement.GetProperty("runningProcessCount").GetInt32());
    }

    [Test]
    public async Task UpdateRuntime_WhenModelsRunning_ReturnsConflictWithoutInstalling()
    {
        // The hard pre-update safety gate: a running llama-server process blocks the install with a 409 — the binary
        // is never replaced while a process holds it, and the operator must eject deliberately first (no auto-evict).
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        var supervisor = new FakeProcessSupervisor(FakeProcessSupervisor.RunningChat());
        // The endpoint resolves the catalog asset BEFORE the running-process gate, so the 409 is only reachable once an
        // asset resolves. Stub it: the production catalog queries the live GitHub Releases API, which made this
        // assertion depend on network reachability and the unauthenticated rate limit (it degraded to the sanitized 400
        // whenever the lookup failed). The stub keeps the gate — not the transport — the thing under test.
        var releaseCatalog = Substitute.For<ILlamaCppReleaseCatalog>();
        releaseCatalog.ResolveAssetAsync(Arg.Any<string>(), Arg.Any<OSPlatform>(), Arg.Any<Architecture>(), Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                      .Returns(LlamaCppReleaseResult.ForAsset("b9700",
                          new LlamaCppReleaseAsset("llama-b9700-bin-win-cuda-x64.zip",
                              new Uri("https://example.invalid/llama-b9700-bin-win-cuda-x64.zip"),
                              new string('a', count: 64),
                              Size: 1024)));
        await using var factory = CreateFactory(binaryManager, new LlamaCppUpdateState(), releaseCatalog, supervisor);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/model-fit/llamacpp/update")
        {
            Content = JsonContent.Create(new
            {
                tag = "b9700"
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        AssertEx.Equal(expected: 1, doc.RootElement.GetProperty("runningProcessCount").GetInt32());

        await binaryManager.DidNotReceiveWithAnyArgs()
                           .InstallTagAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateRuntime_WhenOverrideActive_ShortCircuits()
    {
        // With a bring-your-own override active the operator manages the binary out-of-band, so the
        // catalog-driven update is disabled: the endpoint returns an explicit "updates disabled" 409 and never installs.
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        var overrideOptions = new LlamaServerRuntimeOverrideOptions
        {
            ServerPath = "/opt/llama/llama-server",
            Variant = GpuVariant.Cuda
        };

        await using var factory = CreateFactory(binaryManager, new LlamaCppUpdateState(), overrideOptions: overrideOptions);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/model-fit/llamacpp/update")
        {
            Content = JsonContent.Create(new
            {
                tag = "b9700"
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        AssertEx.Contains(doc.RootElement.GetProperty("message").GetString(), "override", StringComparison.OrdinalIgnoreCase);

        await binaryManager.DidNotReceiveWithAnyArgs()
                           .InstallTagAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RuntimeStatus_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = CreateFactory(Substitute.For<ILlamaCppBinaryManager>(), new LlamaCppUpdateState());
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/model-fit/llamacpp/runtime");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static TestingWebAppFactory CreateFactory(ILlamaCppBinaryManager binaryManager,
        ILlamaCppUpdateState updateState,
        ILlamaCppReleaseCatalog? releaseCatalog = null,
        ILlamaServerProcessSupervisor? supervisor = null,
        LlamaServerRuntimeOverrideOptions? overrideOptions = null,
        InstalledRuntimeState? installedRuntime = null)
    {
        return new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ILlamaCppBinaryManager>();
                services.AddSingleton(binaryManager);
                services.RemoveAll<ILlamaCppUpdateState>();
                services.AddSingleton(updateState);

                // Hermetic installed-runtime state. The production IInstalledRuntimeStore reads a MACHINE-GLOBAL file
                // (LocalApplicationData/XE-Local-AI-Engine/installed-runtime.json) that the test host's per-test
                // NodeData override does NOT isolate — so on a dev box that has done an in-app source build the real
                // record (sourceBuildPath set) leaks in and the mapper suppresses updateAvailable ([archMED-2/4]:
                // source builds are off the prebuilt channel). Inject a fake store so the snapshot alone drives the
                // asserted updateAvailable/upstream fields. Default: no installed runtime (not a source build).
                var installedStore = Substitute.For<IInstalledRuntimeStore>();
                installedStore.ReadAsync(Arg.Any<CancellationToken>()).Returns(installedRuntime);
                services.RemoveAll<IInstalledRuntimeStore>();
                services.AddSingleton(installedStore);

                if (releaseCatalog is not null)
                {
                    services.RemoveAll<ILlamaCppReleaseCatalog>();
                    services.AddSingleton(releaseCatalog);
                }

                if (overrideOptions is not null)
                {
                    services.RemoveAll<LlamaServerRuntimeOverrideOptions>();
                    services.AddSingleton(overrideOptions);
                }

                // Deterministic running-process surface for the running-count + 409 safety-gate assertions (a real
                // supervisor would have no processes on a fresh test host; a fake lets a test simulate "models running").
                if (supervisor is not null)
                {
                    services.RemoveAll<ILlamaServerProcessSupervisor>();
                    services.AddSingleton(supervisor);
                }
            }
        };
    }
}
