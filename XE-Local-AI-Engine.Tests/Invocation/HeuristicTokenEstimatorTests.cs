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
