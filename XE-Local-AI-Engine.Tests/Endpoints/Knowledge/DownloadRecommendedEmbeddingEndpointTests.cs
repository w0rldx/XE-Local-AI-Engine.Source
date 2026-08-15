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
///     Endpoint tests for the one-click recommended-embedding download — the fix for a fresh node being unable to index
///     ANY knowledge-base document until an embedding GGUF was found and fetched by hand. Mirrors
///     <see cref="DownloadRecommendedRerankerEndpointTests" />, plus the one behaviour that deliberately differs: an
///     already-installed NON-recommended embedding model is also a no-op, because that is the model
///     <c>EmbeddingModelResolver</c> would pick anyway.
/// </summary>
public sealed class DownloadRecommendedEmbeddingEndpointTests
{
    private const string DownloadRoute = "/api/local/v1/knowledge-base/embedding/download-recommended";

    [Test]
    public async Task DownloadRecommended_WhenNotInstalled_StartsRecommendedEmbeddingDownload()
    {
        var coordinator = new RecordingDownloadCoordinator(alreadyInFlight: false);

        await using var factory = CreateFactory(coordinator, installed: []);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, DownloadRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DownloadRecommendedEmbeddingResponse>().ConfigureAwait(false);
        AssertEx.NotNull(body);

        AssertEx.Equal(1, coordinator.StartCalls.Count);
        AssertEx.Equal(RecommendedEmbeddingModel.RepoId, coordinator.StartCalls[0].RepoId);
        AssertEx.Equal(RecommendedEmbeddingModel.Quant, coordinator.StartCalls[0].Quant);

        AssertEx.Equal(RecommendedEmbeddingModel.CanonicalModelName, body!.ModelName);
        AssertEx.Equal(RecommendedEmbeddingModel.RepoId, body.RepoId);
        AssertEx.Equal(RecommendedEmbeddingModel.Quant, body.Quant);
        AssertEx.False(body.AlreadyInstalled);
        AssertEx.False(body.AlreadyInFlight);
    }

    [Test]
    public async Task DownloadRecommended_RequestsTheEmbeddingRole_SoTheServerSpawnsWithEmbeddingFlags()
    {
        // The reranker has no role of its own; this one does. An embedding model spawned with chat-role flags would not
        // expose /v1/embeddings, so the role hint on the request is load-bearing rather than cosmetic.
        var coordinator = new RecordingDownloadCoordinator(alreadyInFlight: false);

        await using var factory = CreateFactory(coordinator, installed: []);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, DownloadRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(1, coordinator.StartCalls.Count);
        AssertEx.Equal(GgufRole.Embedding, coordinator.StartCalls[0].Role);
    }

    [Test]
    public async Task DownloadRecommended_WhenRecommendedAlreadyInstalled_IsFriendlyNoOp()
    {
        var coordinator = new RecordingDownloadCoordinator(alreadyInFlight: false);

        await using var factory = CreateFactory(coordinator, installed: [Gguf(RecommendedEmbeddingModel.CanonicalModelName)]);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, DownloadRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DownloadRecommendedEmbeddingResponse>().ConfigureAwait(false);
        AssertEx.NotNull(body);

        AssertEx.Empty(coordinator.StartCalls);
        AssertEx.True(body!.AlreadyInstalled);
        AssertEx.False(body.AlreadyInFlight);
        AssertEx.Equal(RecommendedEmbeddingModel.CanonicalModelName, body.ModelName);
    }

    [Test]
    public async Task DownloadRecommended_WhenADifferentEmbeddingModelIsInstalled_IsAlsoANoOp()
    {
        // EmbeddingModelResolver picks the first installed embedding-NAMED model, so this node can already embed.
        // Downloading a second embedder would consume bandwidth and disk without changing which model gets used.
        var coordinator = new RecordingDownloadCoordinator(alreadyInFlight: false);

        await using var factory = CreateFactory(coordinator, installed: [Gguf("mixedbread-ai/mxbai-embed-large-v1-GGUF:Q4_K_M")]);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, DownloadRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DownloadRecommendedEmbeddingResponse>().ConfigureAwait(false);
        AssertEx.NotNull(body);

        AssertEx.Empty(coordinator.StartCalls);
        AssertEx.True(body!.AlreadyInstalled);
        // The reported name is the model that would actually be used, NOT the recommendation.
        AssertEx.Equal("mixedbread-ai/mxbai-embed-large-v1-GGUF:Q4_K_M", body.ModelName);
        AssertEx.Equal(RecommendedEmbeddingModel.RepoId, body.RepoId);
    }

    [Test]
    public async Task DownloadRecommended_WhenOnlyChatModelsInstalled_StillStartsTheDownload()
    {
        // The guard against a false "already installed": a node full of chat GGUFs still cannot embed, which is exactly
        // the state the live evaluation hit. If the endpoint mistook a chat model for an embedder, the button would
        // silently do nothing and the KB would stay broken.
        var coordinator = new RecordingDownloadCoordinator(alreadyInFlight: false);

        await using var factory = CreateFactory(coordinator, installed:
        [
            Gguf("unsloth/gpt-oss-20b-GGUF:Q5_K_M"),
            Gguf("unsloth/gemma-4-12b-it-GGUF:Q5_K_M")
        ]);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, DownloadRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DownloadRecommendedEmbeddingResponse>().ConfigureAwait(false);
        AssertEx.NotNull(body);

        AssertEx.Equal(1, coordinator.StartCalls.Count);
        AssertEx.False(body!.AlreadyInstalled);
    }

    [Test]
    public async Task DownloadRecommended_WhenOnlyARerankerIsInstalled_StillStartsTheDownload()
    {
        // bge-reranker-v2-m3 matches the BGE- embedding PREFIX, so a naive name check would classify the recommended
        // reranker as an embedder and leave the node unable to index. ModelKindDetector checks reranker first; this
        // pins that ordering from the endpoint's side.
        var coordinator = new RecordingDownloadCoordinator(alreadyInFlight: false);

        await using var factory = CreateFactory(coordinator, installed: [Gguf(RecommendedRerankerModel.CanonicalModelName)]);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, DownloadRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DownloadRecommendedEmbeddingResponse>().ConfigureAwait(false);
        AssertEx.NotNull(body);

        AssertEx.Equal(1, coordinator.StartCalls.Count);
        AssertEx.False(body!.AlreadyInstalled);
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
        var body = await response.Content.ReadFromJsonAsync<DownloadRecommendedEmbeddingResponse>().ConfigureAwait(false);
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
            "The recommended embedding model is no longer published under that repository.");
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
    // AlreadyInFlight, so a test can assert the exact repo/quant/role the endpoint requested.
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
