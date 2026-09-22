namespace XE_Local_AI_Engine.Tests.Endpoints.Knowledge;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The document list carries the embedding-model verdict the INGESTION lane resolves, which is what decides whether
///     an upload can be indexed. Classifying the installed models by name is a different truth.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ListKnowledgeDocumentsEndpointTests
{
    private const string ListRoute = "/api/local/v1/knowledge-base/documents";

    [Test]
    public async Task List_WhenResolutionIsConfident_ReportsTheResolvedModelAsAvailable()
    {
        // A custom name no name-based classifier would call an embedding model; the resolver matched it, so it works.
        var body = await GetListAsync(new EmbeddingModelResolution { Name = "house-vectors:Q4_K_M", IsConfident = true });

        AssertEx.Equal("house-vectors:Q4_K_M", body.GetProperty("embeddingModel").GetString());
        AssertEx.True(body.GetProperty("embeddingModelAvailable").GetBoolean(),
            "A confident resolution means ingestion can embed, so the upload gate must be open.");
    }

    [Test]
    public async Task List_WhenResolutionIsNotConfident_ReportsTheConfiguredModelAsUnavailable()
    {
        var body = await GetListAsync(new EmbeddingModelResolution { Name = "nomic-embed-text", IsConfident = false });

        AssertEx.Equal("nomic-embed-text", body.GetProperty("embeddingModel").GetString());
        AssertEx.False(body.GetProperty("embeddingModelAvailable").GetBoolean(),
            "Nothing installed matched, so an accepted upload would fail later in the background embedder.");
    }

    private static async Task<JsonElement> GetListAsync(EmbeddingModelResolution resolution)
    {
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                var resolver = Substitute.For<IEmbeddingModelResolver>();
                resolver.ResolveAsync(Arg.Any<ILocalModelProvider>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(resolution));
                services.RemoveAll<IEmbeddingModelResolver>();
                services.AddSingleton(resolver);
            }
        };
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ListRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
