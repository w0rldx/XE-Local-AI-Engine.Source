namespace XE_Local_AI_Engine.Tests.Knowledge;

using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class AdaptiveRetrievalPolicyTests
{
    [Test]
    public void DecideRerank_WhenArmsAgree_SkipsOptionalModel()
    {
        var best = Guid.NewGuid();

        var decision = AdaptiveRetrievalPolicy.DecideRerank(true,
            true,
            [new RankFusionInput(best, 4), new RankFusionInput(Guid.NewGuid(), 1)],
            [new RankFusionInput(best, 0.9), new RankFusionInput(Guid.NewGuid(), 0.2)],
            candidateCount: 3,
            elapsed: TimeSpan.FromMilliseconds(10),
            latencyBudget: TimeSpan.FromMilliseconds(500));

        AssertEx.False(decision.ShouldRerank);
        AssertEx.Equal(AdaptiveRerankReason.ArmAgreement, decision.Reason);
    }

    [Test]
    public void DecideRerank_WhenArmsDisagree_Reranks()
    {
        var decision = AdaptiveRetrievalPolicy.DecideRerank(true,
            true,
            [new RankFusionInput(Guid.NewGuid(), 4)],
            [new RankFusionInput(Guid.NewGuid(), 0.9)],
            candidateCount: 3,
            elapsed: TimeSpan.FromMilliseconds(10),
            latencyBudget: TimeSpan.FromMilliseconds(500));

        AssertEx.True(decision.ShouldRerank);
        AssertEx.Equal(AdaptiveRerankReason.Ambiguous, decision.Reason);
    }

    [Test]
    public void DecideRerank_WhenOptionalBudgetSpent_SkipsReranker()
    {
        var decision = AdaptiveRetrievalPolicy.DecideRerank(true,
            true,
            [new RankFusionInput(Guid.NewGuid(), 4)],
            [new RankFusionInput(Guid.NewGuid(), 0.9)],
            candidateCount: 3,
            elapsed: TimeSpan.FromMilliseconds(400),
            latencyBudget: TimeSpan.FromMilliseconds(500));

        AssertEx.False(decision.ShouldRerank);
        AssertEx.Equal(AdaptiveRerankReason.LatencyBudget, decision.Reason);
    }

    [Test]
    public void DecideRerank_WhenAdaptiveDisabled_PreservesAlwaysRerankMode()
    {
        var best = Guid.NewGuid();

        var decision = AdaptiveRetrievalPolicy.DecideRerank(false,
            true,
            [new RankFusionInput(best, 4)],
            [new RankFusionInput(best, 0.9)],
            candidateCount: 2,
            elapsed: TimeSpan.FromSeconds(2),
            latencyBudget: TimeSpan.FromMilliseconds(500));

        AssertEx.True(decision.ShouldRerank);
        AssertEx.Equal(AdaptiveRerankReason.Forced, decision.Reason);
    }
}
