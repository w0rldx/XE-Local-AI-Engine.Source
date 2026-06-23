namespace XE_Local_AI_Engine.Tests.Analysis;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Analysis;
using XE_Local_AI_Engine.Client.Services.Analysis.Implementation;
using XE_Local_AI_Engine.Client.Services.Insights;
using XE_Local_AI_Engine.Tests.Testing;

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

        var outcome = await service.AnalyzeAsync(agentId).ConfigureAwait(false);

        AssertEx.False(outcome.AgentExists, "A null aggregate must surface AgentExists == false (the endpoint 404s).");
        AssertEx.False(outcome.MeetsThreshold);
        AssertEx.Equal(expected: 0, outcome.CreatedSuggestions.Count);
        AssertEx.Equal(expected: 0, agent.InvocationCount, "The agent must not be invoked for a missing agent.");
        await actionService.DidNotReceive()
                           .CreateAnalysisSuggestionAsync(Arg.Any<PlaybookAnalysisSuggestionInput>(), Arg.Any<CancellationToken>())
                           .ConfigureAwait(false);
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

        var outcome = await service.AnalyzeAsync(agentId).ConfigureAwait(false);

        AssertEx.True(outcome.AgentExists);
        AssertEx.False(outcome.MeetsThreshold, "Sub-threshold feedback must report MeetsThreshold == false.");
        AssertEx.Equal(expected: 0, outcome.CreatedSuggestions.Count);
        AssertEx.Equal(expected: 0, agent.InvocationCount, "Sub-threshold runs never invoke the model.");
        await actionService.DidNotReceive()
                           .CreateAnalysisSuggestionAsync(Arg.Any<PlaybookAnalysisSuggestionInput>(), Arg.Any<CancellationToken>())
                           .ConfigureAwait(false);
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

        var outcome = await service.AnalyzeAsync(agentId).ConfigureAwait(false);

        AssertEx.True(outcome.AgentExists);
        AssertEx.True(outcome.MeetsThreshold);
        AssertEx.Equal(expected: 2, outcome.ProposedCount);
        AssertEx.Equal(expected: 2, outcome.CreatedSuggestions.Count);
        AssertEx.Equal(expected: 0, outcome.RejectedCount);
        AssertEx.Equal(expected: 0, outcome.DuplicateCount);
        await actionService.Received(2)
                           .CreateAnalysisSuggestionAsync(Arg.Any<PlaybookAnalysisSuggestionInput>(), Arg.Any<CancellationToken>())
                           .ConfigureAwait(false);
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

        var outcome = await service.AnalyzeAsync(agentId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, outcome.ProposedCount);
        AssertEx.Equal(expected: 0, outcome.CreatedSuggestions.Count);
        AssertEx.Equal(expected: 1, outcome.RejectedCount, "A proposal with no evidence must be rejected.");
        await actionService.DidNotReceive()
                           .CreateAnalysisSuggestionAsync(Arg.Any<PlaybookAnalysisSuggestionInput>(), Arg.Any<CancellationToken>())
                           .ConfigureAwait(false);
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

        var outcome = await service.AnalyzeAsync(agentId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, outcome.ProposedCount);
        AssertEx.Equal(expected: 0, outcome.CreatedSuggestions.Count);
        AssertEx.Equal(expected: 1, outcome.RejectedCount, "A proposal citing evidence not in the aggregate must be rejected.");
        await actionService.DidNotReceive()
                           .CreateAnalysisSuggestionAsync(Arg.Any<PlaybookAnalysisSuggestionInput>(), Arg.Any<CancellationToken>())
                           .ConfigureAwait(false);
    }

    [Test]
    public async Task AnalyzeAsync_WhenProposalDuplicatesExistingEnabledAction_SkipsIt()
    {
        var agentId = Guid.NewGuid();
        var exemplar = Exemplar();
        var insightsResult = BuildInsights(agentId, meetsThreshold: true, [exemplar]);

        // An existing Enabled action whose (scope, behavior) the proposal normalizes to (case/whitespace-insensitive).
        var existing = new PlaybookActionRecord(Guid.NewGuid(),
            agentId,
            PlaybookActionState.Enabled,
            PlaybookActionSource.Manual,
            TriggerCondition: null,
            "Cite sources before answering.",
            "search",
            Priority: 10,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);

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

        var outcome = await service.AnalyzeAsync(agentId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, outcome.ProposedCount);
        AssertEx.Equal(expected: 0, outcome.CreatedSuggestions.Count);
        AssertEx.Equal(expected: 1, outcome.DuplicateCount, "A near-duplicate of an existing live action must be skipped.");
        AssertEx.Equal(expected: 0, outcome.RejectedCount);
        await actionService.DidNotReceive()
                           .CreateAnalysisSuggestionAsync(Arg.Any<PlaybookAnalysisSuggestionInput>(), Arg.Any<CancellationToken>())
                           .ConfigureAwait(false);
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
                         return Task.FromResult(new PlaybookActionRecord(Guid.NewGuid(),
                             input.AgentDefinitionId,
                             PlaybookActionState.Suggested,
                             PlaybookActionSource.Analysis,
                             input.TriggerCondition,
                             input.Behavior,
                             input.Scope,
                             input.Priority,
                             Version: 1,
                             CreatedAtUtc: 10,
                             UpdatedAtUtc: 10,
                             input.SourceFeedbackIds,
                             input.Confidence));
                     });
    }

    private static FeedbackInsightsResult BuildInsights(Guid agentId, bool meetsThreshold, IReadOnlyList<FeedbackExemplarView> exemplars)
    {
        return new FeedbackInsightsResult(agentId,
            "Agent",
            GeneratedAtUtc: 1_000,
            MinOccurrenceThreshold: 3,
            new OverallFeedback(Total: 5, Up: 1, Down: 4, DownRate: 0.8d, meetsThreshold),
            [],
            exemplars);
    }

    private static FeedbackExemplarView Exemplar()
    {
        return new FeedbackExemplarView("down", "needs better citations", Guid.NewGuid(), Guid.NewGuid(), CreatedAtUtc: 100, Truncated: false);
    }

    private static ProposedPlaybookAction Proposal(IReadOnlyList<Guid> sourceFeedbackIds,
        double confidence,
        string behavior = "Prefer the existing shared helper.",
        string? scope = null)
    {
        return new ProposedPlaybookAction(behavior, TriggerCondition: null, scope, sourceFeedbackIds, confidence);
    }

    private sealed class FakeAnalysisAgent(Func<FeedbackInsightsResult, IReadOnlyList<ProposedPlaybookAction>> propose) : IPlaybookAnalysisAgent
    {
        public int InvocationCount { get; private set; }

        public Task<IReadOnlyList<ProposedPlaybookAction>> ProposeAsync(FeedbackInsightsResult aggregate, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return Task.FromResult(propose(aggregate));
        }
    }
}
