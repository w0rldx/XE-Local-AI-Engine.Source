namespace XE_Local_AI_Engine.Tests.Invocation;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The token-estimation formula is DELIBERATELY duplicated across assemblies: the application-layer
///     <see cref="HeuristicTokenEstimator" /> (used by the outer conversation budgeter) and the AI.Agent-layer
///     <c>ProviderMessageTokenEstimator</c> (used by the provider-boundary budget middleware). This cross-assembly parity
///     test locks the two copies together so a change to one that is not mirrored in the other fails here — including the
///     script-aware weighting (ASCII / Latin accents / CJK / emoji).
/// </summary>
public sealed class TokenEstimatorParityTests
{
    [Test]
    public void BothEstimators_ProduceIdenticalMessageEstimates_AcrossScripts()
    {
        var heuristic = new HeuristicTokenEstimator();
        foreach (var text in Samples())
        {
            var message = new ChatMessage(ChatRole.User, [new TextContent(text)]);
            AssertEx.Equal(ProviderMessageTokenEstimator.EstimateTokens(message), heuristic.EstimateTokens(message),
                $"The application and AI.Agent estimators must agree for input: '{text}'.");
        }
    }

    [Test]
    public void BothEstimators_WeightCjkHeavierThanLatinAccents()
    {
        // Guards the shared intent directly (not just parity): CJK must weigh more than European accents in BOTH copies.
        var heuristic = new HeuristicTokenEstimator();
        var accent = new ChatMessage(ChatRole.User, [new TextContent(new string('é', 32))]);
        var cjk = new ChatMessage(ChatRole.User, [new TextContent(new string('语', 32))]);

        AssertEx.True(heuristic.EstimateTokens(cjk) > heuristic.EstimateTokens(accent), "Application estimator must weight CJK above Latin accents.");
        AssertEx.True(ProviderMessageTokenEstimator.EstimateTokens(cjk) > ProviderMessageTokenEstimator.EstimateTokens(accent),
            "AI.Agent estimator must weight CJK above Latin accents.");
    }

    [Test]
    public void BothEstimators_ProduceIdenticalCalibratedEstimates_WithoutWeakeningCjkWeight()
    {
        var calibrations = new TokenEstimatorCalibrationStore();
        calibrations.SetDivisor("model-a", charsPerToken: 7);
        var heuristic = new HeuristicTokenEstimator(calibrations);

        foreach (var text in Samples())
        {
            var message = new ChatMessage(ChatRole.User, [new TextContent(text)]);
            AssertEx.Equal(ProviderMessageTokenEstimator.EstimateTokens(message, charsPerToken: 7),
                heuristic.EstimateTokens(message, "model-a"));
        }

        var cjk = new ChatMessage(ChatRole.User, [new TextContent(new string('中', 32))]);
        AssertEx.Equal(expected: 36, heuristic.EstimateTokens(cjk, "model-a"));
    }

    [Test]
    public void BothEstimators_WeightTheGapBelowCompatibilityIdeographs_AtTheLighterNonAsciiWeight()
    {
        // Regression for the range-bound divergence this parity suite missed: the AI.Agent copy's compatibility-block
        // lower bound was the literal ideograph U+8C48 instead of its VISUALLY IDENTICAL compatibility clone U+F900,
        // so U+A000–U+F8FF (Yi syllables, private-use chars) was CJK-weighted there but accent-weighted in the
        // application copy. Pin the ABSOLUTE weight in both (32 chars at weight 2 → (32*2)/4 + 4 = 20 tokens; the CJK
        // weight would give 36), not just parity — parity alone would re-pass if both copies drifted together.
        var heuristic = new HeuristicTokenEstimator();
        foreach (var character in new[]
                 {
                     '\uA000',
                     '\uA490',
                     '\uE000',
                     '\uF8FF'
                 })
        {
            var message = new ChatMessage(ChatRole.User, [new TextContent(new string(character, 32))]);
            AssertEx.Equal(expected: ((32 * 2) / 4) + 4, heuristic.EstimateTokens(message),
                $"Application estimator must give U+{(int)character:X4} the lighter non-ASCII weight.");
            AssertEx.Equal(expected: ((32 * 2) / 4) + 4, ProviderMessageTokenEstimator.EstimateTokens(message),
                $"AI.Agent estimator must give U+{(int)character:X4} the lighter non-ASCII weight.");
        }
    }

    [Test]
    public void BothEstimators_WeightCompatibilityIdeographBlockBounds_AtCjkWeight()
    {
        // Both ends of U+F900–U+FAFF stay CJK-weighted in both copies, pinning the escaped bounds against
        // off-by-one drift: 32 chars at weight 4 → (32*4)/4 + 4 = 36 tokens.
        var heuristic = new HeuristicTokenEstimator();
        foreach (var character in new[]
                 {
                     '\uF900',
                     '\uFAFF'
                 })
        {
            var message = new ChatMessage(ChatRole.User, [new TextContent(new string(character, 32))]);
            AssertEx.Equal(expected: ((32 * 4) / 4) + 4, heuristic.EstimateTokens(message),
                $"Application estimator must give U+{(int)character:X4} the CJK weight.");
            AssertEx.Equal(expected: ((32 * 4) / 4) + 4, ProviderMessageTokenEstimator.EstimateTokens(message),
                $"AI.Agent estimator must give U+{(int)character:X4} the CJK weight.");
        }
    }

    private static IEnumerable<string> Samples()
    {
        yield return string.Empty;
        yield return "plain ascii sentence with punctuation!";
        yield return "Grüße aus München — schöne, schnöde Straße";
        yield return "机器学习模型需要大量训练数据";
        yield return "mixed 中文 and ascii with emoji 😀🔥 tail";
        yield return new string(' ', 8);
        // The U+A000–U+F8FF gap between the unified-CJK and compatibility-ideograph blocks (Yi syllables, private-use
        // chars) plus the compatibility block itself — the region where the two copies' range bounds once diverged.
        yield return "yi \uA000\uA001\uA490 and private-use \uE000\uF8FF in the once-divergent gap";
        yield return "compatibility ideographs \uF900\uFA00\uFAFF at the block bounds";
    }
}
