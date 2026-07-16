namespace XE_Local_AI_Engine.Tests.Invocation;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
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

    private static IEnumerable<string> Samples()
    {
        yield return string.Empty;
        yield return "plain ascii sentence with punctuation!";
        yield return "Grüße aus München — schöne, schnöde Straße";
        yield return "机器学习模型需要大量训练数据";
        yield return "mixed 中文 and ascii with emoji 😀🔥 tail";
        yield return new string(' ', 8);
    }
}
