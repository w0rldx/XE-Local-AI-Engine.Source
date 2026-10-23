namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class TurnKnowledgeSourceCollectorTests
{
    [Test]
    public void Snapshot_WithoutAScope_IsEmptyAndAddIsANoOp()
    {
        TurnKnowledgeSourceCollector.Add(Hit("orphan"));

        AssertEx.Empty(TurnKnowledgeSourceCollector.Snapshot());
    }

    [Test]
    public void Add_KeepsFirstSeenOrderAndListsARepeatedChunkOnce()
    {
        var first = Hit("first");
        var second = Hit("second");
        using var scope = TurnKnowledgeSourceCollector.BeginScope();

        TurnKnowledgeSourceCollector.Add(first);
        TurnKnowledgeSourceCollector.Add(second);
        TurnKnowledgeSourceCollector.Add(first);

        AssertEx.Equal("first,second", Titles(TurnKnowledgeSourceCollector.Snapshot()));
    }

    [Test]
    public void Dispose_RestoresTheOuterScope()
    {
        using var outer = TurnKnowledgeSourceCollector.BeginScope();
        TurnKnowledgeSourceCollector.Add(Hit("outer"));

        using (TurnKnowledgeSourceCollector.BeginScope())
        {
            TurnKnowledgeSourceCollector.Add(Hit("inner"));
            AssertEx.Equal("inner", Titles(TurnKnowledgeSourceCollector.Snapshot()));
        }

        AssertEx.Equal("outer", Titles(TurnKnowledgeSourceCollector.Snapshot()));
    }

    [Test]
    public async Task TwoConcurrentFlows_EachSeeOnlyTheirOwnHits()
    {
        using var bothStarted = new Barrier(participantCount: 2);

        var flows = await Task.WhenAll(RunFlowAsync("a"), RunFlowAsync("b"));

        AssertEx.Equal("a-0,a-1,a-2", flows[0]);
        AssertEx.Equal("b-0,b-1,b-2", flows[1]);

        Task<string> RunFlowAsync(string name)
        {
            return Task.Run(() =>
            {
                using var scope = TurnKnowledgeSourceCollector.BeginScope();
                bothStarted.SignalAndWait(TimeSpan.FromSeconds(10));
                for (var index = 0; index < 3; index++)
                {
                    TurnKnowledgeSourceCollector.Add(Hit($"{name}-{index}"));
                }

                return Titles(TurnKnowledgeSourceCollector.Snapshot());
            });
        }
    }

    [Test]
    public void Merge_PutsPreTurnSourcesFirstDeduplicatesByChunkAndCaps()
    {
        var shared = TurnKnowledgeSourceCollector.ToSource(Hit("shared"));
        IReadOnlyList<NodeChatMessageSource> preTurn = [shared, TurnKnowledgeSourceCollector.ToSource(Hit("pre"))];
        IReadOnlyList<NodeChatMessageSource> collected =
        [
            shared with
            {
                Title = "shared-again"
            },
            .. Enumerable.Range(0, 30).Select(index => TurnKnowledgeSourceCollector.ToSource(Hit($"tool-{index}")))
        ];

        var merged = AssertEx.NotNull(TurnKnowledgeSourceCollector.Merge(preTurn, collected));

        AssertEx.Equal(TurnKnowledgeSourceCollector.MaxPersistedSources, merged.Count);
        AssertEx.Equal("shared,pre,tool-0", Titles(merged.Take(3)));
    }

    [Test]
    public void Merge_WhenBothAreEmpty_ReturnsNullSoPersistedSourcesStay()
    {
        AssertEx.Null(TurnKnowledgeSourceCollector.Merge(null, []));
        AssertEx.Null(TurnKnowledgeSourceCollector.Merge([], []));
    }

    private static string Titles(IEnumerable<NodeChatMessageSource> sources)
    {
        return string.Join(',', sources.Select(static source => source.Title));
    }

    private static KnowledgeSearchHit Hit(string title)
    {
        return new KnowledgeSearchHit
        {
            DocumentId = Guid.NewGuid(),
            ChunkId = Guid.NewGuid(),
            Title = title,
            Section = null,
            Content = title,
            Source = "knowledge-base",
            Score = 1.0,
            ChunkIndex = 0,
            DocumentStatus = KnowledgeDocumentStatus.Indexed,
            ServingLastKnownGood = false
        };
    }
}
