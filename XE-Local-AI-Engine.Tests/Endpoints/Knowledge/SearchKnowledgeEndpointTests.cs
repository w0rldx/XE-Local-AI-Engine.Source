namespace XE_Local_AI_Engine.Tests.Endpoints.Knowledge;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Wire-shape tests for <c>POST knowledge-base/search</c>: each hit carries its score together with the score kind,
///     serialized as the enum name the SPA branches on.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class SearchKnowledgeEndpointTests
{
    private const string SearchRoute = "/api/local/v1/knowledge-base/search";

    [Test]
    [Arguments(KnowledgeScoreKind.Fusion, "Fusion")]
    [Arguments(KnowledgeScoreKind.Rerank, "Rerank")]
    public async Task Search_MapsScoreKindAsEnumName(KnowledgeScoreKind scoreKind, string expectedWireValue)
    {
        var searchService = Substitute.For<IKnowledgeSearchService>();
        searchService.SearchAsync(Arg.Any<KnowledgeSearchRequest>(), Arg.Any<CancellationToken>())
                     .Returns(new KnowledgeSearchResult
                     {
                         Results =
                         [
                             new KnowledgeSearchHit
                             {
                                 DocumentId = Guid.NewGuid(),
                                 ChunkId = Guid.NewGuid(),
                                 Title = "Runbook",
                                 Section = null,
                                 Content = "restart the node",
                                 Source = "knowledge_base",
                                 Score = 6.75,
                                 ScoreKind = scoreKind,
                                 ChunkIndex = 0,
                                 DocumentStatus = KnowledgeDocumentStatus.Indexed,
                                 ServingLastKnownGood = false
                             }
                         ]
                     });

        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                // Scoped, as in production, so the override introduces no captive dependency.
                services.RemoveAll<IKnowledgeSearchService>();
                services.AddScoped(_ => searchService);
            }
        };
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, SearchRoute)
        {
            Content = JsonContent.Create(new
            {
                query = "restart"
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var hit = body.RootElement.GetProperty("results")[0];
        AssertEx.Equal(6.75, hit.GetProperty("score").GetDouble());
        AssertEx.Equal(expectedWireValue, hit.GetProperty("scoreKind").GetString());
    }
}
