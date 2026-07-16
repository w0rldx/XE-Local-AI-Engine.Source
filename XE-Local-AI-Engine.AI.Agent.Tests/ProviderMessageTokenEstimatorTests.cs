namespace XE_Local_AI_Engine.AI.Agent.Tests;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Directly pins the AI.Agent-layer estimator's script-aware weighting (the twin of the application-layer
///     HeuristicTokenEstimator; see TokenEstimatorParityTests for the cross-assembly lock). CJK weights ≈1 token per
///     character while Latin accents keep the lighter non-ASCII weight so German/French prose is not over-counted.
/// </summary>
public sealed class ProviderMessageTokenEstimatorTests
{
    private const int OverheadTokens = 4;

    [Test]
    public void EstimateTokens_ForAsciiText_IsCharCountOverFourPlusOverhead()
    {
        var message = new ChatMessage(ChatRole.User, [new TextContent(new string('x', 40))]);

        AssertEx.Equal(expected: (40 / 4) + OverheadTokens, ProviderMessageTokenEstimator.EstimateTokens(message));
    }

    [Test]
    public void EstimateTokens_WeightsCjkAtRoughlyOneTokenPerCharacter()
    {
        var message = new ChatMessage(ChatRole.User, [new TextContent(new string('中', 40))]);

        // (40 * 4) / 4 + 4 = 44 (≈1 token/char), versus the old (40 * 2) / 4 + 4 = 24 undercount.
        AssertEx.Equal(expected: ((40 * 4) / 4) + OverheadTokens, ProviderMessageTokenEstimator.EstimateTokens(message));
    }

    [Test]
    public void EstimateTokens_WeightsLatinAccentsLighterThanCjk()
    {
        var german = new ChatMessage(ChatRole.User, [new TextContent(new string('ü', 40))]);
        var cjk = new ChatMessage(ChatRole.User, [new TextContent(new string('中', 40))]);

        AssertEx.Equal(expected: ((40 * 2) / 4) + OverheadTokens, ProviderMessageTokenEstimator.EstimateTokens(german));
        AssertEx.True(ProviderMessageTokenEstimator.EstimateTokens(german) < ProviderMessageTokenEstimator.EstimateTokens(cjk),
            "Latin accents must weigh lighter than CJK in the provider-boundary estimator too.");
    }
}
