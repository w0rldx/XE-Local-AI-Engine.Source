namespace XE_Local_AI_Engine.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class BenchmarkReasoningReadoutTests
{
    private const int Applied = 256;

    [Test]
    public void ReasoningAtBudget_IsTrueFromTheSlackBelowTheAppliedBudget()
    {
        AssertEx.Equal<bool?>(true, BenchmarkReasoningReadout.ReasoningAtBudget(Applied - BenchmarkReasoningReadout.AtBudgetSlackTokens, Applied, 0, 1),
            "a count the slack below the budget still reads at budget");
        AssertEx.Equal<bool?>(true, BenchmarkReasoningReadout.ReasoningAtBudget(Applied, Applied, 0, 1));
        AssertEx.Equal<bool?>(false, BenchmarkReasoningReadout.ReasoningAtBudget(Applied - BenchmarkReasoningReadout.AtBudgetSlackTokens - 1, Applied, 0, 1),
            "one token past the slack is under budget");
    }

    [Test]
    [Arguments(null, 256, 0, 1)]
    [Arguments(255, null, 0, 1)]
    [Arguments(255, 256, 1, 2)]
    [Arguments(255, 256, null, 1)]
    [Arguments(255, 256, 0, 2)]
    [Arguments(255, 256, 0, null)]
    public void ReasoningAtBudget_IsNullWhenACountIsUnknownOrTheTurnWasNotOneRound(int? reasoning, int? applied, int? toolCalls, int? segments)
    {
        AssertEx.Null(BenchmarkReasoningReadout.ReasoningAtBudget(reasoning, applied, toolCalls, segments));
    }

    [Test]
    public void NonReasoningOutputTokens_SubtractsTheReasoningOnASingleRound()
    {
        AssertEx.Equal<int?>(300, BenchmarkReasoningReadout.NonReasoningOutputTokens(556, 256, 0, 1));
        AssertEx.Null(BenchmarkReasoningReadout.NonReasoningOutputTokens(556, 256, 2, 3), "a tool round's generation spans several rounds");
        AssertEx.Null(BenchmarkReasoningReadout.NonReasoningOutputTokens(null, 256, 0, null), "missing usage");
        AssertEx.Null(BenchmarkReasoningReadout.NonReasoningOutputTokens(556, null, 0, 1));
    }

    [Test]
    public void BothReadouts_AreNullForAReasoningToolCallRepromptThatAnsweredDirectly()
    {
        // The final round wrote its tool call into the reasoning, the runner re-prompted once with thinking off, and the
        // retry answered with no tool call: zero executed tools, but two timed provider requests.
        AssertEx.Null(BenchmarkReasoningReadout.ReasoningAtBudget(Applied, Applied, toolCallCount: 0, segmentCount: 2));
        AssertEx.Null(BenchmarkReasoningReadout.NonReasoningOutputTokens(556, 256, toolCallCount: 0, segmentCount: 2));
    }

    [Test]
    public void NonReasoningOutputTokens_IsNeverNegative()
    {
        // Only the retry timed its generation while the reasoning count spans both rounds.
        AssertEx.Null(BenchmarkReasoningReadout.NonReasoningOutputTokens(40, 256, toolCallCount: 0, segmentCount: 1));
    }
}
