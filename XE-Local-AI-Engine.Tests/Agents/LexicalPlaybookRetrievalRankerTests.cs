namespace XE_Local_AI_Engine.Tests.Agents;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
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

        var result = await Ranker.SelectTopKAsync("what is the weather forecast", [cooking, partial, weather], k: 3, CancellationToken.None);

        AssertEx.Equal(expected: 3, result.Count);
        AssertEx.Equal(weather.Id, result[0].Id, "Two shared tokens (weather, forecast) must rank first.");
        AssertEx.Equal(partial.Id, result[1].Id, "One shared token (forecast) ranks above zero overlap.");
        AssertEx.Equal(cooking.Id, result[2].Id, "No shared tokens ranks last.");
    }

    [Test]
    public async Task SelectTopK_ForFixedInput_IsDeterministic()
    {
        var candidates = SampleCandidates();

        var first = await Ranker.SelectTopKAsync("deploy the production build", candidates, k: 2, CancellationToken.None);
        var second = await Ranker.SelectTopKAsync("deploy the production build", candidates, k: 2, CancellationToken.None);

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

        var result = await Ranker.SelectTopKAsync("deploy", [highPriorityNewer, highPriorityOlder, lowPriority], k: 3, CancellationToken.None);

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

        var result = await Ranker.SelectTopKAsync("   ", [third, first, second], k: 3, CancellationToken.None);

        AssertEx.Equal(first.Id, result[0].Id, "Blank query (zero overlap) falls back to Priority ascending.");
        AssertEx.Equal(second.Id, result[1].Id);
        AssertEx.Equal(third.Id, result[2].Id);
    }

    [Test]
    public async Task SelectTopK_WhenCandidatesEmpty_ReturnsEmpty()
    {
        var result = await Ranker.SelectTopKAsync("anything", [], k: 5, CancellationToken.None);

        AssertEx.Equal(expected: 0, result.Count);
    }

    [Test]
    public async Task SelectTopK_WhenKIsNonPositive_ReturnsEmpty()
    {
        var result = await Ranker.SelectTopKAsync("weather", SampleCandidates(), k: 0, CancellationToken.None);

        AssertEx.Equal(expected: 0, result.Count);
    }

    [Test]
    public async Task SelectTopK_WhenQueryIsOnlyFunctionWords_FallsBackToPriorityOrder()
    {
        // Mixed English/German function words tokenise to nothing, so this is the blank-query case: every score is zero
        // and the Priority/CreatedAtUtc order decides, exactly as SelectTopK_WhenQueryIsBlank_FallsBackToPriorityOrder.
        var third = Action("alpha", priority: 30, createdAtUtc: 1);
        var first = Action("beta", priority: 10, createdAtUtc: 5);
        var second = Action("gamma", priority: 20, createdAtUtc: 3);

        var result = await Ranker.SelectTopKAsync("what is it about, und der dann", [third, first, second], k: 3, CancellationToken.None);

        AssertEx.Equal(first.Id, result[0].Id, "A function-word-only query scores zero everywhere and falls back to Priority ascending.");
        AssertEx.Equal(second.Id, result[1].Id);
        AssertEx.Equal(third.Id, result[2].Id);
    }

    [Test]
    public async Task SelectTopK_WhenNoisyCandidateRepeatsQueryWords_PrefersTheShortExactMatch()
    {
        // Both share the same two content words with the query, so the raw overlap count tied them and the noisy
        // playbook won on its lower Priority. The square-root length divisor breaks that: 2/sqrt(2) beats 2/sqrt(13).
        var exact = Action("deploy production", priority: 50, createdAtUtc: 2);
        var noisy = Action("deploy production rollout checklist staging canary rollback approval window monitoring dashboard incident escalation",
            priority: 1,
            createdAtUtc: 1);

        var result = await Ranker.SelectTopKAsync("deploy production", [noisy, exact], k: 2, CancellationToken.None);

        AssertEx.Equal(exact.Id, result[0].Id, "A short exact match must outrank a long trigger that merely contains the same words.");
        AssertEx.Equal(noisy.Id, result[1].Id);
    }

    [Test]
    public async Task SelectTopK_WhenOnlyFunctionWordsOverlap_RanksTheTopicalCandidateFirst()
    {
        // Under a raw overlap count both scored 3 — the decoy purely on "and", "then" and "the" — and the decoy's lower
        // Priority won the tie. With function words dropped the decoy scores zero and the topical playbook wins.
        var decoy = Action("and then archive the logs and notify the team", priority: 1, createdAtUtc: 1);
        var topical = Action("restart the node", priority: 90, createdAtUtc: 2);

        var result = await Ranker.SelectTopKAsync("and then restart the node", [decoy, topical], k: 2, CancellationToken.None);

        AssertEx.Equal(topical.Id, result[0].Id, "Function words must not carry a candidate past the one that shares real content words.");
        AssertEx.Equal(decoy.Id, result[1].Id);
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
            "behaviour text",
            Scope: null,
            priority,
            Version: 1,
            createdAtUtc,
            createdAtUtc);
    }
}
