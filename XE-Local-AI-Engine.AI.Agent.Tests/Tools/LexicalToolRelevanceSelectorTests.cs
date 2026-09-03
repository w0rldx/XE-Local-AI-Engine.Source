namespace XE_Local_AI_Engine.AI.Agent.Tests.Tools;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The shipped, model-free tool-relevance selector. Two properties matter more than the ranking itself: the fast
///     path at or below the threshold returns the whole array untouched (the byte-identical default), and the emitted
///     order is always the INPUT order, so a fixed selected set serialises to the same tools array on every round of a
///     turn — which is what keeps the llama.cpp prompt prefix and its compiled GBNF grammar stable.
/// </summary>
public sealed class LexicalToolRelevanceSelectorTests
{
    private const int Threshold = 12;

    [Test]
    public async Task SelectAsync_AtOrBelowTheThreshold_ReturnsEveryNameWithoutRanking()
    {
        var candidates = BuildNonCore(Threshold);

        var selection = await BuildSelector().SelectAsync("write a file", candidates, Threshold, CancellationToken.None);

        AssertEx.Equal(Threshold, selection.OfferedNames.Count, "At the threshold the whole array is offered.");
        AssertEx.Empty(selection.HiddenNames, "Nothing may be held back on the fast path.");
        AssertEx.True(selection.OfferedNames.SequenceEqual(candidates.Select(static candidate => candidate.Name), StringComparer.Ordinal),
            "The fast path returns the input order verbatim.");
    }

    [Test]
    public async Task SelectAsync_WithABlankQuery_ReturnsEveryName()
    {
        var candidates = BuildNonCore(Threshold + 8);

        var selection = await BuildSelector().SelectAsync("   ", candidates, Threshold, CancellationToken.None);

        AssertEx.Equal(candidates.Count, selection.OfferedNames.Count, "A blank query has nothing to rank against.");
        AssertEx.Empty(selection.HiddenNames);
    }

    [Test]
    public async Task SelectAsync_AboveTheThreshold_NeverTrimsACoreTool()
    {
        List<ToolRelevanceCandidate> candidates =
        [
            new("ask_user", "Asks the user a question.", IsCore: true),
            new("record_finding", "Records a finding.", IsCore: true),
            .. BuildNonCore(count: 20)
        ];

        var selection = await BuildSelector().SelectAsync("unrelated words", candidates, Threshold, CancellationToken.None);

        AssertEx.Contains(selection.OfferedNames, "ask_user");
        AssertEx.Contains(selection.OfferedNames, "record_finding");
        AssertEx.False(selection.HiddenNames.Contains("ask_user", StringComparer.Ordinal), "A core tool is never a ranking candidate.");
        AssertEx.False(selection.HiddenNames.Contains("record_finding", StringComparer.Ordinal), "A core tool is never a ranking candidate.");
    }

    [Test]
    public async Task SelectAsync_WhenEveryCandidateIsCoreAndExceedsTheThreshold_HidesNothing()
    {
        List<ToolRelevanceCandidate> candidates = [.. Enumerable.Range(0, 20).Select(index => new ToolRelevanceCandidate($"core_{index}", "A core tool.", IsCore: true))];

        var selection = await BuildSelector().SelectAsync("anything", candidates, Threshold, CancellationToken.None);

        AssertEx.Equal(candidates.Count, selection.OfferedNames.Count, "With nothing rankable the whole core is still offered.");
        AssertEx.Empty(selection.HiddenNames);
    }

    [Test]
    public async Task SelectAsync_WhenTheCoreLeavesFewerThanSixSlots_StillFillsSix()
    {
        // Core of nine — a skills-bearing work-session agent — leaves 12 - 9 = 3 slots by the bare threshold. The floor
        // raises the fill to six, so the ranker is always choosing among a meaningful set, and the offer may exceed the
        // threshold: the threshold is a trigger, not a cap.
        List<ToolRelevanceCandidate> candidates =
        [
            .. Enumerable.Range(0, 9).Select(index => new ToolRelevanceCandidate($"core_{index}", "A core tool.", IsCore: true)),
            .. BuildNonCore(count: 20)
        ];

        var selection = await BuildSelector().SelectAsync("read the file", candidates, Threshold, CancellationToken.None);

        AssertEx.Equal(expected: 15, selection.OfferedNames.Count, "Nine core plus the six-slot floor.");
        AssertEx.Equal(expected: 14, selection.HiddenNames.Count, "The remaining non-core tools are held back.");
    }

    [Test]
    public async Task SelectAsync_CalledTwiceWithTheSameInput_EmitsTheIdenticalOrder()
    {
        var candidates = BuildNonCore(count: 25);
        var selector = BuildSelector();

        var first = await selector.SelectAsync("read the knowledge base", candidates, Threshold, CancellationToken.None);
        var second = await selector.SelectAsync("read the knowledge base", candidates, Threshold, CancellationToken.None);

        AssertEx.True(first.OfferedNames.SequenceEqual(second.OfferedNames, StringComparer.Ordinal), "Selection is deterministic and re-sorted into input order.");
        AssertEx.True(first.HiddenNames.SequenceEqual(second.HiddenNames, StringComparer.Ordinal));
    }

    [Test]
    public async Task SelectAsync_WhenScoresTie_BreaksByTheCandidateIndex()
    {
        // Every candidate scores zero against a query sharing no token with any of them, so the tie-break — the input
        // index — is the whole ordering, and the first twelve win the twelve floored slots.
        var candidates = BuildNonCore(count: 30);

        var selection = await BuildSelector().SelectAsync("zzzz", candidates, Threshold, CancellationToken.None);

        AssertEx.True(selection.OfferedNames.SequenceEqual(candidates.Take(Threshold).Select(static candidate => candidate.Name), StringComparer.Ordinal),
            "An all-zero score set falls back to the input order.");
    }

    [Test]
    public async Task SelectAsync_AboveTheThreshold_PrefersTheCandidateTheQueryNames()
    {
        List<ToolRelevanceCandidate> candidates =
        [
            .. BuildNonCore(count: 20),
            new("search_knowledge_base", "Searches the node knowledge base for matching passages.", IsCore: false)
        ];

        var selection = await BuildSelector().SelectAsync("search the knowledge base for the pin", candidates, Threshold, CancellationToken.None);

        AssertEx.Contains(selection.OfferedNames, "search_knowledge_base", "The ranker exists to keep the tool the query is about.");
    }

    private static LexicalToolRelevanceSelector BuildSelector()
    {
        return new LexicalToolRelevanceSelector(Options.Create(new ToolRelevanceOptions()));
    }

    private static List<ToolRelevanceCandidate> BuildNonCore(int count)
    {
        return [.. Enumerable.Range(0, count).Select(index => new ToolRelevanceCandidate($"tool_{index}", $"Filler tool number {index}.", IsCore: false))];
    }
}
