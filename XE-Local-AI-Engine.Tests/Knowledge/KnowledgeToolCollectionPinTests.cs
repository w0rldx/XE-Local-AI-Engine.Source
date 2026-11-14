namespace XE_Local_AI_Engine.Tests.Knowledge;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     A chat the operator hard-picked one knowledge collection for: every knowledge-base tool may read only that
///     collection, an omitted collectionId means it, and a call naming another is refused before any read runs.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class KnowledgeToolCollectionPinTests
{
    private const string Pinned = "PROJECT-A";

    [Test]
    [Arguments("""{"query":"needle","collectionId":"project-b"}""")]
    [Arguments("""{"query":"needle","collectionId":"DEFAULT"}""")]
    public async Task Search_NamingAnotherCollectionThanThePin_IsRefusedWithoutSearching(string arguments)
    {
        var searchService = Substitute.For<IKnowledgeSearchService>();
        var handler = CreateSearchHandler(searchService);

        using var scope = TurnKnowledgeSourceCollector.BeginScope(Pinned);
        var response = await handler.ExecuteAsync(arguments);

        AssertRefusedAsAToolError(response);
        await searchService.DidNotReceiveWithAnyArgs().SearchAsync(default!, default);
    }

    [Test]
    [Arguments("""{"query":"needle"}""")]
    [Arguments("""{"query":"needle","collectionId":"project-a"}""")]
    public async Task Search_OmittingTheCollectionOrNamingThePin_SearchesOnlyThePinnedCollection(string arguments)
    {
        var searchService = Substitute.For<IKnowledgeSearchService>();
        searchService.SearchAsync(Arg.Any<KnowledgeSearchRequest>(), Arg.Any<CancellationToken>())
                     .Returns(new KnowledgeSearchResult
                     {
                         Results = []
                     });
        var handler = CreateSearchHandler(searchService);

        using var scope = TurnKnowledgeSourceCollector.BeginScope(Pinned);
        _ = await handler.ExecuteAsync(arguments);

        await searchService.Received(1)
                           .SearchAsync(Arg.Is<KnowledgeSearchRequest>(static request => request.CollectionId == Pinned && !request.AllCollections),
                               Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("""{"query":"needle"}""")]
    [Arguments("""{"query":"needle","collectionId":"  "}""")]
    public async Task Search_WithoutAPin_OmittingTheCollection_SearchesEveryCollection(string arguments)
    {
        // "All collections" means the agent chooses: naming no collection is the widest choice, not DEFAULT.
        var searchService = Substitute.For<IKnowledgeSearchService>();
        searchService.SearchAsync(Arg.Any<KnowledgeSearchRequest>(), Arg.Any<CancellationToken>())
                     .Returns(new KnowledgeSearchResult
                     {
                         Results = []
                     });
        var handler = CreateSearchHandler(searchService);

        using var scope = TurnKnowledgeSourceCollector.BeginScope();
        _ = await handler.ExecuteAsync(arguments);

        await searchService.Received(1).SearchAsync(Arg.Is<KnowledgeSearchRequest>(static request => request.AllCollections), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Search_WithoutAPin_NamingACollection_SearchesOnlyThatCollection()
    {
        var searchService = Substitute.For<IKnowledgeSearchService>();
        searchService.SearchAsync(Arg.Any<KnowledgeSearchRequest>(), Arg.Any<CancellationToken>())
                     .Returns(new KnowledgeSearchResult
                     {
                         Results = []
                     });
        var handler = CreateSearchHandler(searchService);

        using var scope = TurnKnowledgeSourceCollector.BeginScope();
        _ = await handler.ExecuteAsync("""{"query":"needle","collectionId":"project-b"}""");

        await searchService.Received(1)
                           .SearchAsync(Arg.Is<KnowledgeSearchRequest>(static request => request.CollectionId == "PROJECT-B" && !request.AllCollections),
                               Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReadDocument_NamingAnotherCollectionThanThePin_IsRefusedWithoutReading()
    {
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        var services = new ServiceCollection();
        services.AddScoped(_ => catalog);
        var handler = new ReadDocumentToolHandler(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), StubNodeRuntimeSettings.Create().Build());

        using var scope = TurnKnowledgeSourceCollector.BeginScope(Pinned);
        var response = await handler.ExecuteAsync(JsonSerializer.Serialize(new
        {
            documentId = Guid.NewGuid(),
            collectionId = "PROJECT-B"
        }));

        AssertRefusedAsAToolError(response);
        AssertEx.Empty(catalog.ReceivedCalls());
    }

    [Test]
    public async Task ReadSurroundingChunks_NamingAnotherCollectionThanThePin_IsRefusedWithoutReading()
    {
        var expansion = Substitute.For<IContextExpansionService>();
        var services = new ServiceCollection();
        services.AddScoped(_ => expansion);
        var handler = new ReadSurroundingChunksToolHandler(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            StubNodeRuntimeSettings.Create().Build());

        using var scope = TurnKnowledgeSourceCollector.BeginScope(Pinned);
        var response = await handler.ExecuteAsync(JsonSerializer.Serialize(new
        {
            documentId = Guid.NewGuid(),
            collectionId = "PROJECT-B",
            chunkIndex = 0
        }));

        AssertRefusedAsAToolError(response);
        AssertEx.Empty(expansion.ReceivedCalls());
    }

    // The refusal sentence, in the failure shape the invocation loop records as isError, so the model reads it as a failed call.
    private static void AssertRefusedAsAToolError(string response)
    {
        AssertEx.Contains(response, $"can only read the knowledge collection '{Pinned}'");
        AssertEx.Equal(ToolFailureText.ToolReportedCode, ToolFailureText.GetCodeOrNull(response), "A refused call must come back as a tool error, not a successful result.");
    }

    private static SearchKnowledgeBaseToolHandler CreateSearchHandler(IKnowledgeSearchService searchService)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => searchService);
        return new SearchKnowledgeBaseToolHandler(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), StubNodeRuntimeSettings.Create().Build());
    }
}
