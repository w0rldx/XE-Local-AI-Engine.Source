namespace XE_Local_AI_Engine.Tests.Knowledge;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Tools;
using XE_Local_AI_Engine.Client.Services.Knowledge.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

[Category(TestCategories.Unit)]
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
        await catalog.Received(1).GetAsync(documentId, "PROJECT-B", Arg.Any<CancellationToken>());
        await catalog.DidNotReceive().GetAsync(documentId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReadSurroundingChunks_UsesCollectionScopedExpansion()
    {
        var documentId = Guid.NewGuid();
        var expansion = Substitute.For<IContextExpansionService>();
        expansion.ExpandAsync(documentId, 3, ReadSurroundingChunksToolDefinition.DefaultNeighbors, "PROJECT-B", Arg.Any<CancellationToken>())
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
                       .ExpandAsync(documentId, 3, ReadSurroundingChunksToolDefinition.DefaultNeighbors, "PROJECT-B", Arg.Any<CancellationToken>());
        await expansion.DidNotReceive()
                       .ExpandAsync(documentId, 3, ReadSurroundingChunksToolDefinition.DefaultNeighbors, Arg.Any<CancellationToken>());
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

    [Test]
    [Arguments(null, null, 0, 6, 6)]
    [Arguments(6, null, 6, 6, 12)]
    [Arguments(18, 12, 18, 2, null)]
    [Arguments(null, 50, 0, 12, 12)]
    [Arguments(99, null, 99, 0, null)]
    [Arguments(-4, 2, 0, 2, 2)]
    public async Task ReadDocument_PagesByOffsetAndLimit(int? offset, int? limit, int expectedOffset, int expectedReturned, int? expectedNextOffset)
    {
        var detail = CreateDetail(chunkCount: 20, chunkChars: 10);
        var handler = CreateReadDocumentHandler(CatalogReturning(detail));

        var response = await handler.ExecuteAsync(JsonSerializer.Serialize(new
        {
            documentId = detail.DocumentId,
            collectionId = detail.CollectionId,
            offset,
            limit
        }));

        using var payload = JsonDocument.Parse(response);
        var root = payload.RootElement;
        AssertEx.Equal(20, root.GetProperty("totalChunks").GetInt32());
        AssertEx.Equal(expectedOffset, root.GetProperty("offset").GetInt32());
        AssertEx.Equal(expectedReturned, root.GetProperty("returnedChunks").GetInt32());
        var next = root.GetProperty("nextOffset");
        AssertEx.Equal(expectedNextOffset, next.ValueKind == JsonValueKind.Null ? null : next.GetInt32());
        var indexes = root.GetProperty("chunks").EnumerateArray().Select(static chunk => chunk.GetProperty("chunkIndex").GetInt32()).ToList();
        AssertEx.Equal(string.Join(',', Enumerable.Range(expectedOffset, expectedReturned)), string.Join(',', indexes));
        AssertEx.False(root.GetProperty("truncated").GetBoolean());
    }

    [Test]
    public async Task ReadDocument_KeepsTheCharacterCapAsTheOuterBoundAndPointsNextOffsetAtTheFirstChunkLeftOut()
    {
        var detail = CreateDetail(chunkCount: 12, chunkChars: 20_000);
        var handler = CreateReadDocumentHandler(CatalogReturning(detail));

        var response = await handler.ExecuteAsync(JsonSerializer.Serialize(new
        {
            documentId = detail.DocumentId,
            collectionId = detail.CollectionId,
            limit = 12
        }));

        using var payload = JsonDocument.Parse(response);
        var root = payload.RootElement;
        AssertEx.True(root.GetProperty("truncated").GetBoolean());
        AssertEx.Equal(2, root.GetProperty("returnedChunks").GetInt32());
        AssertEx.Equal(2, root.GetProperty("nextOffset").GetInt32());
    }

    [Test]
    public async Task ReadSurroundingChunks_ClampsAnOversizedWindowToThreeChunksPerSide()
    {
        var documentId = Guid.NewGuid();
        var expansion = Substitute.For<IContextExpansionService>();
        IReadOnlyList<KnowledgeNeighborChunk> neighbors =
        [
            .. Enumerable.Range(0, 11).Select(static index => new KnowledgeNeighborChunk
            {
                ChunkId = Guid.NewGuid(),
                ChunkIndex = index,
                Content = $"chunk {index}",
                HeadingPath = null
            })
        ];
        expansion.ExpandAsync(documentId, 5, ReadSurroundingChunksToolDefinition.MaxNeighbors, "PROJECT-B", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(neighbors));
        var handler = CreateReadSurroundingHandler(expansion);

        var response = await handler.ExecuteAsync(JsonSerializer.Serialize(new
        {
            documentId,
            collectionId = "PROJECT-B",
            chunkIndex = 5,
            before = 5,
            after = 9
        }));

        using var payload = JsonDocument.Parse(response);
        var indexes = payload.RootElement.GetProperty("chunks").EnumerateArray().Select(static chunk => chunk.GetProperty("chunkIndex").GetInt32()).ToList();
        AssertEx.Equal("2,3,4,5,6,7,8", string.Join(',', indexes));
    }

    [Test]
    public void ReadSchemas_AdvertiseThePagingParametersAndTheirCeilings()
    {
        using var readDocument = JsonDocument.Parse(ReadDocumentToolDefinition.ParameterSchema);
        var documentProperties = readDocument.RootElement.GetProperty("properties");
        AssertEx.Equal(0, documentProperties.GetProperty("offset").GetProperty("minimum").GetInt32());
        AssertEx.Equal(ReadDocumentToolDefinition.MaxLimit, documentProperties.GetProperty("limit").GetProperty("maximum").GetInt32());

        using var surrounding = JsonDocument.Parse(ReadSurroundingChunksToolDefinition.ParameterSchema);
        var surroundingProperties = surrounding.RootElement.GetProperty("properties");
        AssertEx.Equal(3, surroundingProperties.GetProperty("before").GetProperty("maximum").GetInt32());
        AssertEx.Equal(3, surroundingProperties.GetProperty("after").GetProperty("maximum").GetInt32());
    }

    private static KnowledgeDocumentDetail CreateDetail(int chunkCount, int chunkChars)
    {
        return new KnowledgeDocumentDetail
        {
            DocumentId = Guid.NewGuid(),
            DisplayName = "book.pdf",
            Status = KnowledgeDocumentStatus.Indexed,
            FailureReason = null,
            ChunkCount = chunkCount,
            EmbeddingModel = "embedder",
            StaleModel = false,
            SizeBytes = 1,
            CreatedAtUtc = 0,
            UpdatedAtUtc = 0,
            CollectionId = "PROJECT-B",
            Chunks =
            [
                .. Enumerable.Range(0, chunkCount).Select(index => new KnowledgeDocumentChunkView
                {
                    ChunkIndex = index,
                    HeadingPath = null,
                    Content = new string('x', chunkChars)
                })
            ]
        };
    }

    private static IKnowledgeDocumentCatalogService CatalogReturning(KnowledgeDocumentDetail detail)
    {
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        catalog.GetAsync(detail.DocumentId, detail.CollectionId, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<KnowledgeDocumentDetail?>(detail));
        return catalog;
    }

    private static ReadDocumentToolHandler CreateReadDocumentHandler(IKnowledgeDocumentCatalogService catalog)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => catalog);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new ReadDocumentToolHandler(scopeFactory, StubNodeRuntimeSettings.Create().Build());
    }

    private static ReadSurroundingChunksToolHandler CreateReadSurroundingHandler(IContextExpansionService expansion)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => expansion);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new ReadSurroundingChunksToolHandler(scopeFactory, StubNodeRuntimeSettings.Create().Build());
    }
}
