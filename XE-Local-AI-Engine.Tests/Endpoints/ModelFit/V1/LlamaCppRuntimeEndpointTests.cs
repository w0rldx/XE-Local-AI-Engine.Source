namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Providers.LlamaServer;
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
        // node-settings accessor (the authoritative "recommended" value, locked decision #1), not the snapshot.
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
        updateState.Store(new LlamaCppUpdateSnapshot(InstalledTag: "b9692",
            RecommendedTag: "b9700",
            UpstreamLatestTag: "b9777",
            UpdateAvailable: true,
            IsOffline: false,
            CheckedAtUtc: DateTimeOffset.UtcNow));

        await using var factory = CreateFactory(Substitute.For<ILlamaCppBinaryManager>(), updateState, catalog);
        using var client = factory.CreateClient();

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
    public async Task RuntimeStatus_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = CreateFactory(Substitute.For<ILlamaCppBinaryManager>(), new LlamaCppUpdateState());
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/model-fit/llamacpp/runtime");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static TestingWebAppFactory CreateFactory(ILlamaCppBinaryManager binaryManager, ILlamaCppUpdateState updateState, ILlamaCppReleaseCatalog? releaseCatalog = null)
    {
        return new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ILlamaCppBinaryManager>();
                services.AddSingleton(binaryManager);
                services.RemoveAll<ILlamaCppUpdateState>();
                services.AddSingleton(updateState);

                if (releaseCatalog is not null)
                {
                    services.RemoveAll<ILlamaCppReleaseCatalog>();
                    services.AddSingleton(releaseCatalog);
                }
            }
        };
    }
}
