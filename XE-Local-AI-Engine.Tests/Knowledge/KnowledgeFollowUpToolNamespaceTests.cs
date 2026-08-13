namespace XE_Local_AI_Engine.Tests.Knowledge;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Tools;
using XE_Local_AI_Engine.Client.Services.Knowledge.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class KnowledgeFollowUpToolNamespaceTests
{
    [Test]
    public async Task ReadDocument_UsesCollectionScopedCatalogLookup()
    {
        var documentId = Guid.NewGuid();
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        catalog.GetAsync(documentId, "PROJECT-B", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<KnowledgeDocumentDetail?>(null));
        var handler = CreateReadDocumentHandler(catalog);

        var response = await handler.ExecuteAsync(JsonSerializer.Serialize(new
        {
            documentId,
            collectionId = "PROJECT-B"
        }));

        AssertEx.Contains(response, "No knowledge-base document exists");
        await catalog.Received(1).GetAsync(documentId, "PROJECT-B", Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await catalog.DidNotReceive().GetAsync(documentId, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ReadSurroundingChunks_UsesCollectionScopedExpansion()
    {
        var documentId = Guid.NewGuid();
        var expansion = Substitute.For<IContextExpansionService>();
        expansion.ExpandAsync(documentId, 3, 1, "PROJECT-B", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<KnowledgeNeighborChunk>>([]));
        var handler = CreateReadSurroundingHandler(expansion);

        var response = await handler.ExecuteAsync(JsonSerializer.Serialize(new
        {
            documentId,
            collectionId = "PROJECT-B",
            chunkIndex = 3
        }));

        using var payload = JsonDocument.Parse(response);
        AssertEx.Equal("PROJECT-B", payload.RootElement.GetProperty("collectionId").GetString());
        AssertEx.Equal(0, payload.RootElement.GetProperty("chunks").GetArrayLength());
        await expansion.Received(1)
                       .ExpandAsync(documentId, 3, 1, "PROJECT-B", Arg.Any<CancellationToken>())
                       .ConfigureAwait(false);
        await expansion.DidNotReceive()
                       .ExpandAsync(documentId, 3, 1, Arg.Any<CancellationToken>())
                       .ConfigureAwait(false);
    }

    [Test]
    public void FollowUpSchemas_RequireCollectionId()
    {
        foreach (var schema in new[]
                 {
                     ReadDocumentToolDefinition.ParameterSchema,
                     ReadSurroundingChunksToolDefinition.ParameterSchema
                 })
        {
            using var document = JsonDocument.Parse(schema);
            var required = document.RootElement.GetProperty("required")
                                   .EnumerateArray()
                                   .Select(static item => item.GetString())
                                   .ToList();
            AssertEx.Contains(required, "collectionId");
            AssertEx.True(document.RootElement.GetProperty("properties").TryGetProperty("collectionId", out _));
        }
    }

    private static ReadDocumentToolHandler CreateReadDocumentHandler(IKnowledgeDocumentCatalogService catalog)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => catalog);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new ReadDocumentToolHandler(scopeFactory, EnabledOptions());
    }

    private static ReadSurroundingChunksToolHandler CreateReadSurroundingHandler(IContextExpansionService expansion)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => expansion);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new ReadSurroundingChunksToolHandler(scopeFactory, EnabledOptions());
    }

    private static IOptions<KnowledgeBaseOptions> EnabledOptions() =>
        Options.Create(new KnowledgeBaseOptions
        {
            AgentToolsEnabled = true
        });
}
