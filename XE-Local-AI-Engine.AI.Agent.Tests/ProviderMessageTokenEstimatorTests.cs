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

    [Test]
    public void EstimateTokens_WeightsYiAndPrivateUse_AtTheLighterNonAsciiWeight()
    {
        // Regression for the range-bound divergence: this estimator's compatibility-block lower bound was the literal
        // ideograph U+8C48 instead of its VISUALLY IDENTICAL compatibility clone U+F900, silently CJK-weighting the
        // whole U+A000–U+F8FF gap (Yi syllables, private-use chars) only on this side of the mirrored pair.
        foreach (var character in new[] { '\uA000', '\uA490', '\uE000', '\uF8FF' })
        {
            var message = new ChatMessage(ChatRole.User, [new TextContent(new string(character, 40))]);
            AssertEx.Equal(expected: ((40 * 2) / 4) + OverheadTokens,
                ProviderMessageTokenEstimator.EstimateTokens(message),
                $"U+{(int)character:X4} must carry the lighter non-ASCII weight, not the CJK weight.");
        }
    }

    [Test]
    public void EstimateTokens_WeightsCompatibilityIdeographBlockBounds_AtCjkWeight()
    {
        // Both ends of U+F900–U+FAFF stay CJK-weighted, pinning the block against off-by-one drift of the new escapes.
        foreach (var character in new[] { '\uF900', '\uFAFF' })
        {
            var message = new ChatMessage(ChatRole.User, [new TextContent(new string(character, 40))]);
            AssertEx.Equal(expected: ((40 * 4) / 4) + OverheadTokens,
                ProviderMessageTokenEstimator.EstimateTokens(message),
                $"U+{(int)character:X4} must carry the CJK weight.");
        }
    }
}
