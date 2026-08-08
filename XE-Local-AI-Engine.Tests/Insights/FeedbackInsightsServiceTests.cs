namespace XE_Local_AI_Engine.Tests.Insights;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Insights.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class FeedbackInsightsServiceTests
{
    [Test]
    public async Task GetAgentFeedbackInsightsAsync_WhenStoreReturnsNull_ReturnsNull()
    {
        var service = CreateService(out var store, nowUtcMs: 1_000);
        var agentId = Guid.NewGuid();
        store.GetAgentFeedbackAggregateAsync(agentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<AgentFeedbackAggregate?>(null));

        var result = await service.GetAgentFeedbackInsightsAsync(agentId).ConfigureAwait(false);

        AssertEx.Null(result, "A missing agent must surface as null (the endpoint maps it to 404).");
    }

    [Test]
    public async Task GetAgentFeedbackInsightsAsync_RequestsCappedExemplarsAndStampsGeneratedAt()
    {
        var service = CreateService(out var store, nowUtcMs: 4_242);
        var agentId = Guid.NewGuid();
        store.GetAgentFeedbackAggregateAsync(agentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<AgentFeedbackAggregate?>(new AgentFeedbackAggregate(agentId, "Agent", UpCount: 0, DownCount: 0, [], [])));

        var result = AssertEx.NotNull(await service.GetAgentFeedbackInsightsAsync(agentId).ConfigureAwait(false), "Existing agent should produce a result.");

        AssertEx.Equal(expected: 4_242L, result.GeneratedAtUtc);
        AssertEx.Equal(FeedbackInsightsService.MinOccurrenceThreshold, result.MinOccurrenceThreshold);
        // The privacy cap is enforced by passing MaxExemplars to the store, not by trimming afterwards.
        await store.Received(1)
                   .GetAgentFeedbackAggregateAsync(agentId, FeedbackInsightsService.MaxExemplars, Arg.Any<CancellationToken>())
                   .ConfigureAwait(false);
    }

    [Test]
    public async Task GetAgentFeedbackInsightsAsync_WhenEmpty_ReturnsZeroStateBelowThreshold()
    {
        var service = CreateService(out var store, nowUtcMs: 1);
        var agentId = Guid.NewGuid();
        store.GetAgentFeedbackAggregateAsync(agentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<AgentFeedbackAggregate?>(new AgentFeedbackAggregate(agentId, "Agent", UpCount: 0, DownCount: 0, [], [])));

        var result = AssertEx.NotNull(await service.GetAgentFeedbackInsightsAsync(agentId).ConfigureAwait(false), "Existing agent should produce a result.");

        AssertEx.Equal(expected: 0, result.Overall.Total);
        AssertEx.Equal(expected: 0d, result.Overall.DownRate);
        AssertEx.False(result.Overall.MeetsThreshold, "An empty feedback set is never an actionable pattern.");
        AssertEx.Equal(expected: 0, result.ByTool.Count);
        AssertEx.Equal(expected: 0, result.Exemplars.Count);
    }

    [Test]
    public async Task GetAgentFeedbackInsightsAsync_OverallMeetsThresholdOnlyAtMinOccurrences()
    {
        var service = CreateService(out var store, nowUtcMs: 1);
        var below = Guid.NewGuid();
        var meets = Guid.NewGuid();
        store.GetAgentFeedbackAggregateAsync(below, Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<AgentFeedbackAggregate?>(new AgentFeedbackAggregate(below, "A", UpCount: 1, DownCount: 1, [], [])));
        store.GetAgentFeedbackAggregateAsync(meets, Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<AgentFeedbackAggregate?>(new AgentFeedbackAggregate(meets, "A", UpCount: 1, DownCount: 2, [], [])));

        var belowResult = AssertEx.NotNull(await service.GetAgentFeedbackInsightsAsync(below).ConfigureAwait(false), "result");
        var meetsResult = AssertEx.NotNull(await service.GetAgentFeedbackInsightsAsync(meets).ConfigureAwait(false), "result");

        AssertEx.Equal(expected: 2, belowResult.Overall.Total);
        AssertEx.False(belowResult.Overall.MeetsThreshold, "n=2 (< 3) is not yet a pattern.");
        AssertEx.Equal(expected: 3, meetsResult.Overall.Total);
        AssertEx.True(meetsResult.Overall.MeetsThreshold, "n=3 meets the never-act-on-n=1 bar.");
        AssertEx.Equal(Math.Round(2d / 3d, digits: 4), meetsResult.Overall.DownRate);
    }

    [Test]
    public async Task GetAgentFeedbackInsightsAsync_ShapesPerToolBreakdownWithThresholdAndDownRate()
    {
        var service = CreateService(out var store, nowUtcMs: 1);
        var agentId = Guid.NewGuid();
        store.GetAgentFeedbackAggregateAsync(agentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<AgentFeedbackAggregate?>(new AgentFeedbackAggregate(agentId,
                 "Agent",
                 UpCount: 3,
                 DownCount: 1,
                 [new ToolFeedbackCount("search", UpCount: 2, DownCount: 2), new ToolFeedbackCount("calc", UpCount: 1, DownCount: 0)],
                 [])));

        var result = AssertEx.NotNull(await service.GetAgentFeedbackInsightsAsync(agentId).ConfigureAwait(false), "result");

        AssertEx.Equal(expected: 2, result.ByTool.Count);
        AssertEx.Equal("search", result.ByTool[0].ToolName);
        AssertEx.Equal(expected: 4, result.ByTool[0].Total);
        AssertEx.Equal(expected: 0.5d, result.ByTool[0].DownRate);
        AssertEx.True(result.ByTool[0].MeetsThreshold, "search total=4 (>= 3) is a pattern.");
        AssertEx.Equal("calc", result.ByTool[1].ToolName);
        AssertEx.Equal(expected: 1, result.ByTool[1].Total);
        AssertEx.Equal(expected: 0d, result.ByTool[1].DownRate);
        AssertEx.False(result.ByTool[1].MeetsThreshold, "calc total=1 is not a pattern.");
    }

    [Test]
    public async Task GetAgentFeedbackInsightsAsync_TruncatesLongExemplarComments()
    {
        var service = CreateService(out var store, nowUtcMs: 1);
        var agentId = Guid.NewGuid();
        var longComment = new string(c: 'x', FeedbackInsightsService.MaxExemplarCommentLength + 20);
        const string ShortComment = "concise feedback";
        store.GetAgentFeedbackAggregateAsync(agentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<AgentFeedbackAggregate?>(new AgentFeedbackAggregate(agentId,
                 "Agent",
                 UpCount: 0,
                 DownCount: 2,
                 [],
                 [
                     new FeedbackExemplar("down", longComment, Guid.NewGuid(), Guid.NewGuid(), CreatedAtUtc: 200),
                     new FeedbackExemplar("down", ShortComment, Guid.NewGuid(), Guid.NewGuid(), CreatedAtUtc: 100)
                 ])));

        var result = AssertEx.NotNull(await service.GetAgentFeedbackInsightsAsync(agentId).ConfigureAwait(false), "result");

        AssertEx.Equal(expected: 2, result.Exemplars.Count);
        AssertEx.True(result.Exemplars[0].Truncated, "An over-length comment must be flagged truncated.");
        // 280 retained characters + a single ellipsis glyph.
        AssertEx.Equal(FeedbackInsightsService.MaxExemplarCommentLength + 1, result.Exemplars[0].Comment.Length);
        AssertEx.True(result.Exemplars[0].Comment.EndsWith('…'), "A truncated comment ends with an ellipsis.");
        AssertEx.False(result.Exemplars[1].Truncated, "A short comment is not truncated.");
        AssertEx.Equal(ShortComment, result.Exemplars[1].Comment);
    }

    private static FeedbackInsightsService CreateService(out IFeedbackInsightsStore store, long nowUtcMs)
    {
        store = Substitute.For<IFeedbackInsightsStore>();
        return new FeedbackInsightsService(store, new FixedTimeProvider(nowUtcMs));
    }

    private sealed class FixedTimeProvider(long unixMilliseconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
        }
    }
}
