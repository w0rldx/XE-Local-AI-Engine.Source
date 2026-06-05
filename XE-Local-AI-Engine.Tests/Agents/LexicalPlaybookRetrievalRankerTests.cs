namespace XE_Local_AI_Engine.Tests.Agents;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LexicalPlaybookRetrievalRankerTests
{
    private static readonly IPlaybookRetrievalRanker Ranker = new LexicalPlaybookRetrievalRanker();

    [Test]
    public async Task SelectTopK_WithHigherOverlap_RanksMoreRelevantFirst()
    {
        var weather = Action("weather forecast rain temperature", priority: 10, createdAtUtc: 1);
        var cooking = Action("cooking recipe oven bake", priority: 10, createdAtUtc: 2);
        var partial = Action("forecast the weekend", priority: 10, createdAtUtc: 3);

        var result = await Ranker.SelectTopKAsync("what is the weather forecast", [cooking, partial, weather], 3, CancellationToken.None);

        AssertEx.Equal(3, result.Count);
        AssertEx.Equal(weather.Id, result[0].Id, "Two shared tokens (weather, forecast) must rank first.");
        AssertEx.Equal(partial.Id, result[1].Id, "One shared token (forecast) ranks above zero overlap.");
        AssertEx.Equal(cooking.Id, result[2].Id, "No shared tokens ranks last.");
    }

    [Test]
    public async Task SelectTopK_ForFixedInput_IsDeterministic()
    {
        var candidates = SampleCandidates();

        var first = await Ranker.SelectTopKAsync("deploy the production build", candidates, 2, CancellationToken.None);
        var second = await Ranker.SelectTopKAsync("deploy the production build", candidates, 2, CancellationToken.None);

        AssertEx.Equal(first.Count, second.Count);
        for (var index = 0; index < first.Count; index++)
        {
            AssertEx.Equal(first[index].Id, second[index].Id, "Repeated calls on the same input must yield the same order.");
        }
    }

    [Test]
    public async Task SelectTopK_OnScoreTie_BreaksByPriorityThenCreatedAtUtc()
    {
        // All three share exactly one token ("deploy") with the query, so the tiebreak decides the order.
        var lowPriority = Action("deploy", priority: 5, createdAtUtc: 99);
        var highPriorityOlder = Action("deploy", priority: 50, createdAtUtc: 1);
        var highPriorityNewer = Action("deploy", priority: 50, createdAtUtc: 2);

        var result = await Ranker.SelectTopKAsync("deploy", [highPriorityNewer, highPriorityOlder, lowPriority], 3, CancellationToken.None);

        AssertEx.Equal(lowPriority.Id, result[0].Id, "Lower Priority wins the tiebreak.");
        AssertEx.Equal(highPriorityOlder.Id, result[1].Id, "Equal Priority breaks by older CreatedAtUtc.");
        AssertEx.Equal(highPriorityNewer.Id, result[2].Id, "Newer CreatedAtUtc sorts last on a Priority/score tie.");
    }

    [Test]
    public async Task SelectTopK_WhenKExceedsCandidates_ReturnsAll()
    {
        var candidates = SampleCandidates();

        var result = await Ranker.SelectTopKAsync("production deploy", candidates, candidates.Count + 10, CancellationToken.None);

        AssertEx.Equal(candidates.Count, result.Count, "k larger than the candidate count returns every candidate.");
    }

    [Test]
    public async Task SelectTopK_WhenQueryIsBlank_FallsBackToPriorityOrder()
    {
        var third = Action("alpha", priority: 30, createdAtUtc: 1);
        var first = Action("beta", priority: 10, createdAtUtc: 5);
        var second = Action("gamma", priority: 20, createdAtUtc: 3);

        var result = await Ranker.SelectTopKAsync("   ", [third, first, second], 3, CancellationToken.None);

        AssertEx.Equal(first.Id, result[0].Id, "Blank query (zero overlap) falls back to Priority ascending.");
        AssertEx.Equal(second.Id, result[1].Id);
        AssertEx.Equal(third.Id, result[2].Id);
    }

    [Test]
    public async Task SelectTopK_WhenCandidatesEmpty_ReturnsEmpty()
    {
        var result = await Ranker.SelectTopKAsync("anything", [], 5, CancellationToken.None);

        AssertEx.Equal(0, result.Count);
    }

    [Test]
    public async Task SelectTopK_WhenKIsNonPositive_ReturnsEmpty()
    {
        var result = await Ranker.SelectTopKAsync("weather", SampleCandidates(), 0, CancellationToken.None);

        AssertEx.Equal(0, result.Count);
    }

    private static IReadOnlyList<PlaybookActionRecord> SampleCandidates()
    {
        return
        [
            Action("deploy the production build to the cluster", priority: 10, createdAtUtc: 1),
            Action("summarise the meeting notes", priority: 20, createdAtUtc: 2),
            Action("production incident response runbook", priority: 30, createdAtUtc: 3)
        ];
    }

    private static PlaybookActionRecord Action(string triggerCondition, int priority, long createdAtUtc)
    {
        return new PlaybookActionRecord(Guid.NewGuid(),
            Guid.NewGuid(),
            PlaybookActionState.Enabled,
            PlaybookActionSource.Manual,
            triggerCondition,
            Behavior: "behaviour text",
            Scope: null,
            priority,
            Version: 1,
            createdAtUtc,
            UpdatedAtUtc: createdAtUtc);
    }
}
