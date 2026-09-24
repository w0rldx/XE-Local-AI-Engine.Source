namespace XE_Local_AI_Engine.Tests.Analysis;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Analysis;
using XE_Local_AI_Engine.Client.Services.Analysis.Implementation;
using XE_Local_AI_Engine.Client.Services.Insights;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class PlaybookAnalysisServiceTests
{
    [Test]
    public async Task AnalyzeAsync_WhenAgentDoesNotExist_ReportsAgentMissingAndNeverInvokesAgent()
    {
        var agentId = Guid.NewGuid();
        var agent = new FakeAnalysisAgent(_ =>
        [
            Proposal(new[]
            {
                Guid.NewGuid()
            }, confidence: 0.9d)
        ]);
        var service = CreateService(out var insights, out var actionService, agent);
        insights.GetAgentFeedbackInsightsAsync(agentId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<FeedbackInsightsResult?>(null));

        var outcome = await service.AnalyzeAsync(agentId);

        AssertEx.False(outcome.AgentExists, "A null aggregate must surface AgentExists == false (the endpoint 404s).");
        AssertEx.False(outcome.MeetsThreshold);
        AssertEx.Equal(expected: 0, outcome.CreatedSuggestions.Count);
        AssertEx.Equal(expected: 0, agent.InvocationCount, "The agent must not be invoked for a missing agent.");
        await actionService.DidNotReceive()
                           .CreateAnalysisSuggestionAsync(Arg.Any<PlaybookAnalysisSuggestionInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnalyzeAsync_WhenBelowThreshold_DoesNotInvokeAgentAndWritesNothing()
    {
        var agentId = Guid.NewGuid();
        var agent = new FakeAnalysisAgent(_ =>
        [
            Proposal(new[]
            {
                Guid.NewGuid()
            }, confidence: 0.9d)
        ]);
        var service = CreateService(out var insights, out var actionService, agent);
        insights.GetAgentFeedbackInsightsAsync(agentId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<FeedbackInsightsResult?>(BuildInsights(agentId, meetsThreshold: false, [])));

        var outcome = await service.AnalyzeAsync(agentId);

        AssertEx.True(outcome.AgentExists);
        AssertEx.False(outcome.MeetsThreshold, "Sub-threshold feedback must report MeetsThreshold == false.");
        AssertEx.Equal(expected: 0, outcome.CreatedSuggestions.Count);
        AssertEx.Equal(expected: 0, agent.InvocationCount, "Sub-threshold runs never invoke the model.");
        await actionService.DidNotReceive()
                           .CreateAnalysisSuggestionAsync(Arg.Any<PlaybookAnalysisSuggestionInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnalyzeAsync_WithTwoValidProposals_PersistsBothSuggestions()
    {
        var agentId = Guid.NewGuid();
        var firstExemplar = Exemplar();
        var secondExemplar = Exemplar();
        var insightsResult = BuildInsights(agentId, meetsThreshold: true, [firstExemplar, secondExemplar]);

        var agent = new FakeAnalysisAgent(_ =>
        [
            Proposal(new[]
            {
                firstExemplar.MessageId
            }, confidence: 0.8d, "Cite sources before answering.", "search"),
            Proposal(new[]
            {
                secondExemplar.MessageId,
                secondExemplar.ConversationId
            }, confidence: 0.6d, "Avoid speculative claims.", "writing")
        ]);
        var service = CreateService(out var insights, out var actionService, agent);
        insights.GetAgentFeedbackInsightsAsync(agentId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<FeedbackInsightsResult?>(insightsResult));
        actionService.ListByAgentAsync(agentId, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        EchoCreatedSuggestion(actionService);

        var outcome = await service.AnalyzeAsync(agentId);

        AssertEx.True(outcome.AgentExists);
        AssertEx.True(outcome.MeetsThreshold);
        AssertEx.Equal(expected: 2, outcome.ProposedCount);
        AssertEx.Equal(expected: 2, outcome.CreatedSuggestions.Count);
        AssertEx.Equal(expected: 0, outcome.RejectedCount);
        AssertEx.Equal(expected: 0, outcome.DuplicateCount);
        await actionService.Received(2)
                           .CreateAnalysisSuggestionAsync(Arg.Any<PlaybookAnalysisSuggestionInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnalyzeAsync_WhenProposalHasNoEvidence_RejectsAndNeverPersistsIt()
    {
        var agentId = Guid.NewGuid();
        var exemplar = Exemplar();
        var insightsResult = BuildInsights(agentId, meetsThreshold: true, [exemplar]);

        var agent = new FakeAnalysisAgent(_ => [Proposal([], confidence: 0.7d, "Unsupported claim.")]);
        var service = CreateService(out var insights, out var actionService, agent);
        insights.GetAgentFeedbackInsightsAsync(agentId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<FeedbackInsightsResult?>(insightsResult));
        actionService.ListByAgentAsync(agentId, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        EchoCreatedSuggestion(actionService);

        var outcome = await service.AnalyzeAsync(agentId);

        AssertEx.Equal(expected: 1, outcome.ProposedCount);
        AssertEx.Equal(expected: 0, outcome.CreatedSuggestions.Count);
        AssertEx.Equal(expected: 1, outcome.RejectedCount, "A proposal with no evidence must be rejected.");
        await actionService.DidNotReceive()
                           .CreateAnalysisSuggestionAsync(Arg.Any<PlaybookAnalysisSuggestionInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnalyzeAsync_WhenProposalCitesUnknownEvidence_RejectsItAsHallucination()
    {
        var agentId = Guid.NewGuid();
        var exemplar = Exemplar();
        var insightsResult = BuildInsights(agentId, meetsThreshold: true, [exemplar]);

        // The cited id is not present in any exemplar message/conversation id — the model invented evidence.
        var agent = new FakeAnalysisAgent(_ =>
        [
            Proposal(new[]
            {
                Guid.NewGuid()
            }, confidence: 0.95d, "Hallucinated root cause.")
        ]);
        var service = CreateService(out var insights, out var actionService, agent);
        insights.GetAgentFeedbackInsightsAsync(agentId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<FeedbackInsightsResult?>(insightsResult));
        actionService.ListByAgentAsync(agentId, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        EchoCreatedSuggestion(actionService);

        var outcome = await service.AnalyzeAsync(agentId);

        AssertEx.Equal(expected: 1, outcome.ProposedCount);
        AssertEx.Equal(expected: 0, outcome.CreatedSuggestions.Count);
        AssertEx.Equal(expected: 1, outcome.RejectedCount, "A proposal citing evidence not in the aggregate must be rejected.");
        await actionService.DidNotReceive()
                           .CreateAnalysisSuggestionAsync(Arg.Any<PlaybookAnalysisSuggestionInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnalyzeAsync_WhenProposalDuplicatesExistingEnabledAction_SkipsIt()
    {
        var agentId = Guid.NewGuid();
        var exemplar = Exemplar();
        var insightsResult = BuildInsights(agentId, meetsThreshold: true, [exemplar]);

        // An existing Enabled action whose (scope, behavior) the proposal normalizes to (case/whitespace-insensitive).
        var existing = new PlaybookActionRecord
        {
            Id = Guid.NewGuid(),
            AgentDefinitionId = agentId,
            State = PlaybookActionState.Enabled,
            Source = PlaybookActionSource.Manual,
            TriggerCondition = null,
            Behavior = "Cite sources before answering.",
            Scope = "search",
            Priority = 10,
            Version = 1,
            CreatedAtUtc = 10,
            UpdatedAtUtc = 10
        };

        var agent = new FakeAnalysisAgent(_ =>
        [
            Proposal(new[]
            {
                exemplar.MessageId
            }, confidence: 0.8d, "  CITE   sources before ANSWERING.  ", "Search")
        ]);
        var service = CreateService(out var insights, out var actionService, agent);
        insights.GetAgentFeedbackInsightsAsync(agentId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<FeedbackInsightsResult?>(insightsResult));
        actionService.ListByAgentAsync(agentId, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([existing]));
        EchoCreatedSuggestion(actionService);

        var outcome = await service.AnalyzeAsync(agentId);

        AssertEx.Equal(expected: 1, outcome.ProposedCount);
        AssertEx.Equal(expected: 0, outcome.CreatedSuggestions.Count);
        AssertEx.Equal(expected: 1, outcome.DuplicateCount, "A near-duplicate of an existing live action must be skipped.");
        AssertEx.Equal(expected: 0, outcome.RejectedCount);
        await actionService.DidNotReceive()
                           .CreateAnalysisSuggestionAsync(Arg.Any<PlaybookAnalysisSuggestionInput>(), Arg.Any<CancellationToken>());
    }

    private static PlaybookAnalysisService CreateService(out IFeedbackInsightsService insights,
        out IPlaybookActionService actionService,
        IPlaybookAnalysisAgent agent)
    {
        insights = Substitute.For<IFeedbackInsightsService>();
        actionService = Substitute.For<IPlaybookActionService>();
        return new PlaybookAnalysisService(insights,
            agent,
            actionService,
            Options.Create(new PlaybookAnalysisOptions()),
            NullLogger<PlaybookAnalysisService>.Instance);
    }

    private static void EchoCreatedSuggestion(IPlaybookActionService actionService)
    {
        // Echo each accepted suggestion back as a stored record so the service can accumulate CreatedSuggestions.
        actionService.CreateAnalysisSuggestionAsync(Arg.Any<PlaybookAnalysisSuggestionInput>(), Arg.Any<CancellationToken>())
                     .Returns(callInfo =>
                     {
                         var input = callInfo.Arg<PlaybookAnalysisSuggestionInput>();
                         return Task.FromResult(new PlaybookActionRecord
                         {
                             Id = Guid.NewGuid(),
                             AgentDefinitionId = input.AgentDefinitionId,
                             State = PlaybookActionState.Suggested,
                             Source = PlaybookActionSource.Analysis,
                             TriggerCondition = input.TriggerCondition,
                             Behavior = input.Behavior,
                             Scope = input.Scope,
                             Priority = input.Priority,
                             Version = 1,
                             CreatedAtUtc = 10,
                             UpdatedAtUtc = 10,
                             SourceFeedbackIds = input.SourceFeedbackIds,
                             Confidence = input.Confidence
                         });
                     });
    }

    private static FeedbackInsightsResult BuildInsights(Guid agentId, bool meetsThreshold, IReadOnlyList<FeedbackExemplarView> exemplars)
    {
        return new FeedbackInsightsResult
        {
            AgentDefinitionId = agentId,
            AgentName = "Agent",
            GeneratedAtUtc = 1_000,
            MinOccurrenceThreshold = 3,
            Overall = new OverallFeedback
            {
                Total = 5,
                Up = 1,
                Down = 4,
                DownRate = 0.8d,
                MeetsThreshold = meetsThreshold
            },
            ByTool = [],
            Exemplars = exemplars
        };
    }

    private static FeedbackExemplarView Exemplar()
    {
        return new FeedbackExemplarView
        {
            Rating = "down",
            Comment = "needs better citations",
            MessageId = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            CreatedAtUtc = 100,
            Truncated = false
        };
    }

    private static ProposedPlaybookAction Proposal(IReadOnlyList<Guid> sourceFeedbackIds,
        double confidence,
        string behavior = "Prefer the existing shared helper.",
        string? scope = null)
    {
        return new ProposedPlaybookAction
        {
            Behavior = behavior,
            TriggerCondition = null,
            Scope = scope,
            SourceFeedbackIds = sourceFeedbackIds,
            Confidence = confidence
        };
    }

    private sealed class FakeAnalysisAgent : IPlaybookAnalysisAgent
    {
        private readonly Func<FeedbackInsightsResult, IReadOnlyList<ProposedPlaybookAction>> _propose;

        public FakeAnalysisAgent(Func<FeedbackInsightsResult, IReadOnlyList<ProposedPlaybookAction>> propose)
        {
            _propose = propose;
        }

        public int InvocationCount { get; private set; }

        public Task<IReadOnlyList<ProposedPlaybookAction>> ProposeAsync(FeedbackInsightsResult aggregate, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return Task.FromResult(_propose(aggregate));
        }
    }
}
