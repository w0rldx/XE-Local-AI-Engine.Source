namespace XE_Local_AI_Engine.Tests.Knowledge;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class SearchKnowledgeBaseToolHandlerBudgetTests
{
    [Test]
    public async Task ExecuteAsync_WhenHitsExceedBudget_TrimsLowestScoredAndFlagsTruncated()
    {
        // Five hits of ~20K chars each = ~100K, well over the 50K aggregate budget. Results arrive score-descending, so
        // the handler must keep the top hits until the budget is spent and drop the rest, flagging truncation.
        var hits = Enumerable.Range(0, 5)
                             .Select(index => new KnowledgeSearchHit(DocumentId: Guid.NewGuid(),
                                 ChunkId: Guid.NewGuid(),
                                 Title: $"doc-{index}",
                                 Section: null,
                                 Content: new string('x', 20_000),
                                 Source: "knowledge-base",
                                 Score: 1.0 - (index * 0.1),
                                 ChunkIndex: index))
                             .ToList();

        var handler = CreateHandler(new KnowledgeSearchResult(hits));

        var json = await handler.ExecuteAsync("""{"query":"anything"}""");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        AssertEx.True(root.GetProperty("truncated").GetBoolean(), "an over-budget search must flag truncation");
        AssertEx.Equal(expected: 5, root.GetProperty("totalResults").GetInt32());
        var returned = root.GetProperty("returnedResults").GetInt32();
        AssertEx.True(returned < 5, "some low-scored hits must be trimmed");
        AssertEx.True(returned >= 1, "at least the top hit must be returned");
        AssertEx.Equal(returned, root.GetProperty("results").GetArrayLength());
    }

    [Test]
    public async Task ExecuteAsync_WhenHitsWithinBudget_ReturnsAllAndNotTruncated()
    {
        var hits = new List<KnowledgeSearchHit>
        {
            new(Guid.NewGuid(), Guid.NewGuid(), "doc", Section: null, Content: "small", Source: "knowledge-base", Score: 1.0, ChunkIndex: 0)
        };

        var handler = CreateHandler(new KnowledgeSearchResult(hits));

        var json = await handler.ExecuteAsync("""{"query":"anything"}""");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        AssertEx.False(root.GetProperty("truncated").GetBoolean());
        AssertEx.Equal(expected: 1, root.GetProperty("returnedResults").GetInt32());
        AssertEx.Equal(expected: 1, root.GetProperty("totalResults").GetInt32());
    }

    private static SearchKnowledgeBaseToolHandler CreateHandler(KnowledgeSearchResult result)
    {
        var services = new ServiceCollection();
        services.AddScoped<IKnowledgeSearchService>(_ => new FakeKnowledgeSearchService(result));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new SearchKnowledgeBaseToolHandler(scopeFactory, Options.Create(new KnowledgeBaseOptions
        {
            AgentToolsEnabled = true
        }));
    }

    private sealed class FakeKnowledgeSearchService(KnowledgeSearchResult result) : IKnowledgeSearchService
    {
        public Task<KnowledgeSearchResult> SearchAsync(KnowledgeSearchRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(result);
        }
    }
}
