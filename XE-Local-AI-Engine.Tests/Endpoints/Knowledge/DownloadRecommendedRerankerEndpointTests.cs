namespace XE_Local_AI_Engine.Tests.Endpoints.Knowledge;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint tests for the one-click recommended-reranker download: a fresh node triggers the download coordinator with
///     the recommended repo + pinned quant; an already-installed recommended reranker is a friendly no-op (no second
///     download); an in-flight download is rejoined rather than duplicated; and the surface is operator-gated.
/// </summary>
public sealed class DownloadRecommendedRerankerEndpointTests
{
    private const string DownloadRoute = "/api/local/v1/knowledge-base/reranker/download-recommended";

    [Test]
    public async Task DownloadRecommended_WhenNotInstalled_StartsRecommendedRerankerDownload()
    {
        var coordinator = new RecordingDownloadCoordinator(alreadyInFlight: false);

        await using var factory = CreateFactory(coordinator, installed: []);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, DownloadRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DownloadRecommendedRerankerResponse>().ConfigureAwait(false);
        AssertEx.NotNull(body);

        // The download is driven through the coordinator with the code-grounded recommended descriptor.
        AssertEx.Equal(1, coordinator.StartCalls.Count);
        AssertEx.Equal(RecommendedRerankerModel.RepoId, coordinator.StartCalls[0].RepoId);
        AssertEx.Equal(RecommendedRerankerModel.Quant, coordinator.StartCalls[0].Quant);

        AssertEx.Equal(RecommendedRerankerModel.CanonicalModelName, body!.ModelName);
        AssertEx.Equal(RecommendedRerankerModel.RepoId, body.RepoId);
        AssertEx.Equal(RecommendedRerankerModel.Quant, body.Quant);
        AssertEx.False(body.AlreadyInstalled);
        AssertEx.False(body.AlreadyInFlight);
    }

    [Test]
    public async Task DownloadRecommended_WhenAlreadyInstalled_IsFriendlyNoOp()
    {
        var coordinator = new RecordingDownloadCoordinator(alreadyInFlight: false);

        await using var factory = CreateFactory(coordinator, installed: [Gguf(RecommendedRerankerModel.CanonicalModelName)]);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, DownloadRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DownloadRecommendedRerankerResponse>().ConfigureAwait(false);
        AssertEx.NotNull(body);

        // Already present: re-downloading would be wasted bytes, so the coordinator is never touched.
        AssertEx.Empty(coordinator.StartCalls);
        AssertEx.True(body!.AlreadyInstalled);
        AssertEx.False(body.AlreadyInFlight);
        AssertEx.Equal(RecommendedRerankerModel.CanonicalModelName, body.ModelName);
    }

    [Test]
    public async Task DownloadRecommended_WhenDownloadInFlight_RejoinsWithoutDuplicate()
    {
        var coordinator = new RecordingDownloadCoordinator(alreadyInFlight: true);

        await using var factory = CreateFactory(coordinator, installed: []);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, DownloadRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DownloadRecommendedRerankerResponse>().ConfigureAwait(false);
        AssertEx.NotNull(body);

        AssertEx.Equal(1, coordinator.StartCalls.Count);
        AssertEx.True(body!.AlreadyInFlight);
        AssertEx.False(body.AlreadyInstalled);
    }

    [Test]
    public async Task DownloadRecommended_WhenTokenMissing_IsRejected()
    {
        var coordinator = new RecordingDownloadCoordinator(alreadyInFlight: false);

        await using var factory = CreateFactory(coordinator, installed: []);
        using var client = factory.CreateClient();

        // No node bearer token → the operator policy rejects before any download starts.
        using var response = await client.PostAsync(DownloadRoute, content: null).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertEx.Empty(coordinator.StartCalls);
    }

    [Test]
    public async Task DownloadRecommended_WhenTheModelNameIsAlreadyClaimed_IsConflict()
    {
        // The coordinator's synchronous conflict must reach the operator as a 409 through the shared download mapper —
        // before this endpoint had any catch it fell through to a bare 500.
        var coordinator = new RecordingDownloadCoordinator(alreadyInFlight: false, new GgufAcquisitionConflictException());

        await using var factory = CreateFactory(coordinator, installed: []);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, DownloadRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal(1, coordinator.StartCalls.Count);
    }

    [Test]
    public async Task DownloadRecommended_WhenTheRecommendedRepositoryIsMissing_IsNotFound()
    {
        var failure = new HuggingFaceDownloadException(HuggingFaceDownloadFailure.NotFound,
            "The recommended reranker model is no longer published under that repository.");
        var coordinator = new RecordingDownloadCoordinator(alreadyInFlight: false, failure);

        await using var factory = CreateFactory(coordinator, installed: []);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, DownloadRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static TestServerWebAppFactory CreateFactory(IGgufDownloadCoordinator coordinator, IReadOnlyList<LocalModelDescriptor> installed)
    {
        var modelStore = Substitute.For<IGgufModelStore>();
        modelStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult(installed));

        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                // Lifetimes mirror production (both singletons) so no captive dependency is introduced by the override.
                services.RemoveAll<IGgufDownloadCoordinator>();
                services.AddSingleton(coordinator);
                services.RemoveAll<IGgufModelStore>();
                services.AddSingleton(modelStore);
            }
        };
    }

    private static LocalModelDescriptor Gguf(string modelName)
    {
        return new LocalModelDescriptor
        {
            ModelName = modelName,
            ProviderName = "llamacpp",
            IsAvailable = true,
            SizeBytes = 1024,
            ModifiedAt = DateTimeOffset.UnixEpoch,
            MaxContextTokens = null,
            Capabilities = []
        };
    }

    // Hand-written recording fake: records every StartAsync request and returns a ticket with the configured
    // AlreadyInFlight, so a test can assert the exact repo/quant the endpoint requested without NSubstitute Task friction.
    private sealed class RecordingDownloadCoordinator(bool alreadyInFlight, Exception? failure = null) : IGgufDownloadCoordinator
    {
        public List<GgufModelRequest> StartCalls { get; } = [];

        public Task<GgufDownloadTicket> StartAsync(GgufModelRequest request, CancellationToken ct)
        {
            StartCalls.Add(request);
            if (failure is not null)
            {
                return Task.FromException<GgufDownloadTicket>(failure);
            }

            var modelName = string.IsNullOrWhiteSpace(request.Quant) ? request.RepoId : GgufModelName.Format(request.RepoId, request.Quant);
            return Task.FromResult(new GgufDownloadTicket(modelName, alreadyInFlight));
        }

        public bool Cancel(string modelName)
        {
            return false;
        }

        public GgufDownloadStatus? GetStatus(string modelName)
        {
            return null;
        }

        public IReadOnlyList<GgufDownloadStatus> ListStatuses()
        {
            return [];
        }
    }
}
