namespace XE_Local_AI_Engine.Tests.Invocation;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class HeuristicTokenEstimatorTests
{
    // chars/4 + 4-token per-message framing overhead.
    private const int OverheadTokens = 4;

    [Test]
    public void EstimateTokens_ForTextMessage_IsCharCountOverFourPlusOverhead()
    {
        var estimator = new HeuristicTokenEstimator();
        var message = new ChatMessage(ChatRole.User, [new TextContent(new string('x', 40))]);

        var estimate = estimator.EstimateTokens(message);

        AssertEx.Equal(expected: (40 / 4) + OverheadTokens, estimate);
    }

    [Test]
    public void EstimateTokens_CountsToolResultContentLength()
    {
        // The budgeter's truncation savings rely on a tool result's characters counting toward the estimate.
        var estimator = new HeuristicTokenEstimator();
        var message = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", new string('y', 400))]);

        var estimate = estimator.EstimateTokens(message);

        AssertEx.Equal(expected: (400 / 4) + OverheadTokens, estimate);
    }

    [Test]
    public void EstimateTokens_WeightsCjkAtRoughlyOneTokenPerCharacter()
    {
        // A Han character tokenizes to ≈1+ tokens; weighting CJK at CharsPerToken(4) makes the estimate ≈1 token/char.
        var estimator = new HeuristicTokenEstimator();
        var asciiMessage = new ChatMessage(ChatRole.User, [new TextContent(new string('x', 40))]);
        var cjkMessage = new ChatMessage(ChatRole.User, [new TextContent(new string('中', 40))]);

        var asciiEstimate = estimator.EstimateTokens(asciiMessage);
        var cjkEstimate = estimator.EstimateTokens(cjkMessage);

        // ascii: 40/4 + 4 = 14; cjk: (40*4)/4 + 4 = 44 (≈1 token/char).
        AssertEx.Equal(expected: (40 / 4) + OverheadTokens, asciiEstimate);
        AssertEx.Equal(expected: ((40 * 4) / 4) + OverheadTokens, cjkEstimate);
        AssertEx.True(cjkEstimate > asciiEstimate, "CJK content must estimate conservatively higher than ASCII of equal length");
    }

    [Test]
    public void EstimateTokens_WeightsLatinAccentsLighterThanCjk()
    {
        // European accents (this user's locale is German) tokenize far closer to Latin than CJK does, so they keep the
        // lighter NonAsciiCharWeight(2) — German/French prose must NOT be over-counted at the CJK rate.
        var estimator = new HeuristicTokenEstimator();
        var germanMessage = new ChatMessage(ChatRole.User, [new TextContent(new string('ü', 40))]);
        var cjkMessage = new ChatMessage(ChatRole.User, [new TextContent(new string('中', 40))]);

        var germanEstimate = estimator.EstimateTokens(germanMessage);
        var cjkEstimate = estimator.EstimateTokens(cjkMessage);

        // german accent: (40*2)/4 + 4 = 24; strictly between ASCII (14) and CJK (44).
        AssertEx.Equal(expected: ((40 * 2) / 4) + OverheadTokens, germanEstimate);
        AssertEx.True(germanEstimate < cjkEstimate, "Latin accents must weigh lighter than CJK.");
        AssertEx.True(germanEstimate > (40 / 4) + OverheadTokens, "Latin accents must still weigh a little heavier than ASCII.");
    }

    [Test]
    public void EstimateTokens_ForRealisticCjkSentence_NoLongerUnderCountsByHalf()
    {
        // Reference basis: modern byte-pair tokenizers (cl100k / LLaMA family) emit roughly one-or-more tokens per Han
        // character. The OLD weighting (non-ASCII = 2) estimated ~0.5 token/char — a ~2x UNDER-count that could let an
        // over-window request through. Weighting CJK at CharsPerToken(4) yields ≈1 token/char (conservative, upper-biased).
        var estimator = new HeuristicTokenEstimator();
        const string sentence = "机器学习模型需要大量的训练数据才能达到良好的性能表现";
        var message = new ChatMessage(ChatRole.User, [new TextContent(sentence)]);

        var estimate = estimator.EstimateTokens(message);

        var charCount = sentence.Length; // all BMP Han → one code unit each
        var oldUnderCount = ((charCount * 2) / 4) + OverheadTokens; // the pre-fix ~0.5 token/char estimate
        AssertEx.Equal(expected: ((charCount * 4) / 4) + OverheadTokens, estimate);
        AssertEx.True(estimate >= charCount, "CJK content should estimate at least ~1 token per character, not half.");
        AssertEx.True(estimate > oldUnderCount, "The fix must estimate strictly higher than the old ~0.5 token/char undercount.");
    }

    [Test]
    public void EstimateTokens_ForList_SumsPerMessageEstimates()
    {
        var estimator = new HeuristicTokenEstimator();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextContent(new string('a', 40))]),
            new(ChatRole.Assistant, [new TextContent(new string('b', 40))])
        };

        var total = estimator.EstimateTokens(messages);

        AssertEx.Equal(expected: 2 * ((40 / 4) + OverheadTokens), total);
    }
}
