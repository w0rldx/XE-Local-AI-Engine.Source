namespace XE_Local_AI_Engine.Tests.Eval;

using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Eval;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Eval;
using XE_Local_AI_Engine.Client.Services.Eval.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Eval gate orchestration over a SEEDED golden set via fake runner + fake judge (no Ollama). The fake runner keys
///     its output on whether the system prompt contains the candidate behaviour, so a test can stage a regression by
///     scripting the judge to fail the candidate output but pass the baseline output.
/// </summary>
public sealed class PlaybookEvalServiceTests
{
    private const string CandidateBehavior = "Always cite a source.";

    [Test]
    public async Task RunEvalAsync_WhenCandidateRegressesAPriorGoodCase_FailsWithOneRegression()
    {
        var agentId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var goldenCase = JudgeCase(agentId);

        // Baseline output passes; candidate output fails → a regression on this prior-good case.
        var judge = new FakePlaybookEvalJudge((_, text) => !text.Contains("candidate", StringComparison.Ordinal));
        var service = CreateService(agentId, actionId, [goldenCase], judge, out _, out var actionService);

        var outcome = await service.RunEvalAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.True(outcome.ActionFound);
        AssertEx.NotNull(outcome.Result, "A run with golden cases must produce a result.");
        AssertEx.False(outcome.Result!.Passed, "A candidate that regresses a prior-good case must not pass.");
        AssertEx.Equal(1, outcome.Result.RegressedCaseCount);
        AssertEx.Equal(1, outcome.Result.GoldenCaseCount);
        AssertEx.Equal(1, outcome.Result.ActionVersionAtEval);
        await actionService.Received(1)
                           .RecordEvalResultAsync(agentId, actionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
                           .ConfigureAwait(false);
    }

    [Test]
    public async Task RunEvalAsync_WhenCandidateIsClean_Passes()
    {
        var agentId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var goldenCase = JudgeCase(agentId);

        // Both baseline and candidate output pass → no regression → the candidate is clean.
        var judge = new FakePlaybookEvalJudge((_, _) => true);
        var service = CreateService(agentId, actionId, [goldenCase], judge, out _, out _);

        var outcome = await service.RunEvalAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.True(outcome.ActionFound);
        AssertEx.True(outcome.Result!.Passed, "A clean candidate with no regressions must pass.");
        AssertEx.Equal(0, outcome.Result.RegressedCaseCount);
        AssertEx.Equal(1, outcome.Result.GoldenCaseCount);
    }

    [Test]
    public async Task RunEvalAsync_ExercisesTheAssertionScoringPath()
    {
        var agentId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        // An assertion case is scored by the REAL judge's deterministic path (no model call): the required phrase is
        // present in both baseline and candidate output, so the case passes via "assertion".
        var assertion = JsonSerializer.Serialize(new
        {
            requiredPhrases = new[]
            {
                "output"
            },
            forbiddenPhrases = Array.Empty<string>()
        });
        var goldenCase = AssertionCase(agentId, assertion);
        var service = CreateService(agentId, actionId, [goldenCase], new OllamaPlaybookEvalJudge(NullLogger<OllamaPlaybookEvalJudge>.Instance), out _, out _);

        var outcome = await service.RunEvalAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.True(outcome.Result!.Passed);
        AssertEx.Equal("assertion", outcome.Result.Cases[0].ScoredBy);
    }

    [Test]
    public async Task RunEvalAsync_ExercisesTheJudgeScoringPath()
    {
        var agentId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var goldenCase = JudgeCase(agentId);
        var judge = new FakePlaybookEvalJudge((_, _) => true);
        var service = CreateService(agentId, actionId, [goldenCase], judge, out _, out _);

        var outcome = await service.RunEvalAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.Equal("judge", outcome.Result!.Cases[0].ScoredBy);
    }

    [Test]
    public async Task RunEvalAsync_WhenGoldenSetIsEmpty_FailsWithZeroCases()
    {
        var agentId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var service = CreateService(agentId, actionId, [], new FakePlaybookEvalJudge((_, _) => true), out _, out var actionService);

        var outcome = await service.RunEvalAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.True(outcome.ActionFound, "An empty golden set still finds the action; it records a failing result.");
        AssertEx.False(outcome.Result!.Passed, "An empty golden set cannot prove no-regression, so it never passes.");
        AssertEx.Equal(0, outcome.Result.GoldenCaseCount);
        await actionService.Received(1)
                           .RecordEvalResultAsync(agentId, actionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
                           .ConfigureAwait(false);
    }

    [Test]
    public async Task RunEvalAsync_ComposesTheSuggestedActionByPriority_NotLast()
    {
        var agentId = Guid.NewGuid();
        var actionId = Guid.NewGuid();

        // The candidate is LOW priority (10) so it must sort BEFORE this already-enabled action (priority 50). Once
        // promoted, ListEnabledByAgentAsync re-orders by (Priority, CreatedAtUtc), so the eval must compose the
        // candidate in that SAME sorted position — not merely append it last — or the gate scores the wrong prompt.
        var enabledAction = new PlaybookActionRecord(Guid.NewGuid(),
            agentId,
            PlaybookActionState.Enabled,
            PlaybookActionSource.Manual,
            null,
            "Enabled behaviour.",
            null,
            50,
            1,
            5,
            5);

        var captureRunner = new CapturingEvalAgentRunner();
        var goldenCase = JudgeCase(agentId);
        var service = CreateServiceWithRunner(agentId, actionId, 10, [enabledAction], [goldenCase], captureRunner);

        _ = await service.RunEvalAsync(agentId, actionId).ConfigureAwait(false);

        // The candidate prompt (the higher-priority system prompt the runner saw) places the LOW-priority candidate
        // behaviour BEFORE the enabled one, matching post-promotion injection order.
        var candidatePrompt = captureRunner.CandidatePrompt;
        AssertEx.NotNull(candidatePrompt, "A run with golden cases must compose a candidate prompt.");
        var candidateIndex = candidatePrompt!.IndexOf(CandidateBehavior, StringComparison.Ordinal);
        var enabledIndex = candidatePrompt.IndexOf("Enabled behaviour.", StringComparison.Ordinal);
        AssertEx.True(candidateIndex >= 0, "The candidate behaviour must appear in the candidate prompt.");
        AssertEx.True(enabledIndex >= 0, "The enabled behaviour must appear in the candidate prompt.");
        AssertEx.True(candidateIndex < enabledIndex, "The LOW-priority candidate must compose BEFORE the enabled action, not last.");
    }

    [Test]
    public async Task RunEvalAsync_WhenGoldenSetExceedsCap_RecordsTotalBeforeTruncation()
    {
        var agentId = Guid.NewGuid();
        var actionId = Guid.NewGuid();

        // Seed more golden cases than the per-run cap; the run evaluates the cap but records the full enabled total.
        var goldenCases = Enumerable.Range(0, 5).Select(_ => JudgeCase(agentId)).ToList();
        var service = CreateService(agentId, actionId, goldenCases, new FakePlaybookEvalJudge((_, _) => true), out _, out _, 3);

        var outcome = await service.RunEvalAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.Equal(3, outcome.Result!.GoldenCaseCount);
        AssertEx.Equal(5, outcome.Result.GoldenCaseTotal);
    }

    [Test]
    public async Task RunEvalAsync_WhenActionIsNotAPendingSuggestion_ReportsActionNotFound()
    {
        var agentId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var actionService = Substitute.For<IPlaybookActionService>();
        actionService.LoadPendingSuggestionAsync(agentId, actionId, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<PlaybookActionRecord?>(null));
        var service = CreateServiceWith(actionService, Substitute.For<IPlaybookActionStore>(),
            Substitute.For<IAgentDefinitionStore>(), Substitute.For<IGoldenConversationStore>(),
            new FakePlaybookEvalAgentRunner(), new FakePlaybookEvalJudge((_, _) => true));

        var outcome = await service.RunEvalAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.False(outcome.ActionFound, "A non-pending action must surface ActionFound == false (the endpoint 404s).");
        AssertEx.Null(outcome.Result);
        await actionService.DidNotReceive()
                           .RecordEvalResultAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                           .ConfigureAwait(false);
    }

    private static PlaybookEvalService CreateService(Guid agentId,
        Guid actionId,
        IReadOnlyList<GoldenConversationRecord> goldenCases,
        IPlaybookEvalJudge judge,
        out IPlaybookEvalAgentRunner runner,
        out IPlaybookActionService actionService,
        int maxGoldenCases = 25)
    {
        runner = new FakePlaybookEvalAgentRunner();
        return CreateServiceCore(agentId, actionId, 100, [], goldenCases, runner, judge, maxGoldenCases, out actionService);
    }

    private static PlaybookEvalService CreateServiceWithRunner(Guid agentId,
        Guid actionId,
        int suggestedPriority,
        IReadOnlyList<PlaybookActionRecord> enabledActions,
        IReadOnlyList<GoldenConversationRecord> goldenCases,
        IPlaybookEvalAgentRunner runner)
    {
        return CreateServiceCore(agentId, actionId, suggestedPriority, enabledActions, goldenCases, runner,
            new FakePlaybookEvalJudge((_, _) => true), 25, out _);
    }

    private static PlaybookEvalService CreateServiceCore(Guid agentId,
        Guid actionId,
        int suggestedPriority,
        IReadOnlyList<PlaybookActionRecord> enabledActions,
        IReadOnlyList<GoldenConversationRecord> goldenCases,
        IPlaybookEvalAgentRunner runner,
        IPlaybookEvalJudge judge,
        int maxGoldenCases,
        out IPlaybookActionService actionService)
    {
        var pending = new PlaybookActionRecord(actionId,
            agentId,
            PlaybookActionState.Suggested,
            PlaybookActionSource.Analysis,
            null,
            CandidateBehavior,
            null,
            suggestedPriority,
            1,
            10,
            10,
            [Guid.NewGuid()],
            0.6d);

        actionService = Substitute.For<IPlaybookActionService>();
        actionService.LoadPendingSuggestionAsync(agentId, actionId, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<PlaybookActionRecord?>(pending));
        actionService.RecordEvalResultAsync(agentId, actionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<PlaybookActionRecord?>(pending));

        var actionStore = Substitute.For<IPlaybookActionStore>();
        actionStore.ListEnabledByAgentAsync(agentId, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult(enabledActions));

        var agentStore = Substitute.For<IAgentDefinitionStore>();
        agentStore.GetByIdAsync(agentId, Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<AgentDefinitionRecord?>(CreateAgent(agentId)));

        var goldenStore = Substitute.For<IGoldenConversationStore>();
        goldenStore.ListEnabledByAgentAsync(agentId, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult(goldenCases));

        return CreateServiceWith(actionService, actionStore, agentStore, goldenStore, runner, judge, maxGoldenCases);
    }

    private static PlaybookEvalService CreateServiceWith(IPlaybookActionService actionService,
        IPlaybookActionStore actionStore,
        IAgentDefinitionStore agentStore,
        IGoldenConversationStore goldenStore,
        IPlaybookEvalAgentRunner runner,
        IPlaybookEvalJudge judge,
        int maxGoldenCases = 25)
    {
        var localModelProvider = Substitute.For<ILocalModelProvider>();
        localModelProvider.ProviderName.Returns("ollama");
        localModelProvider.CreateChatClient(Arg.Any<LocalModelSelection>()).Returns(Substitute.For<IChatClient>());
        var providerResolver = SingleProviderResolverFactory.Create(localModelProvider);

        return new PlaybookEvalService(actionService,
            actionStore,
            agentStore,
            goldenStore,
            runner,
            judge,
            providerResolver,
            TimeProvider.System,
            Options.Create(new PlaybookEvalOptions
            {
                ModelName = "test-model",
                MaxGoldenCases = maxGoldenCases
            }),
            NullLogger<PlaybookEvalService>.Instance);
    }

    private static GoldenConversationRecord JudgeCase(Guid agentId)
    {
        return new GoldenConversationRecord(Guid.NewGuid(),
            agentId,
            "Judge case",
            """[{"role":"user","text":"hello"}]""",
            null,
            "The answer must be helpful.",
            true,
            10,
            10);
    }

    private static GoldenConversationRecord AssertionCase(Guid agentId, string assertion)
    {
        return new GoldenConversationRecord(Guid.NewGuid(),
            agentId,
            "Assertion case",
            """[{"role":"user","text":"hello"}]""",
            assertion,
            null,
            true,
            10,
            10);
    }

    private static AgentDefinitionRecord CreateAgent(Guid agentId)
    {
        return new AgentDefinitionRecord(agentId,
            "Builder",
            null,
            "Base instructions.",
            null,
            null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(),
            null,
            1,
            10,
            10);
    }

    /// <summary>
    ///     Scripted runner: returns "candidate output" when the system prompt contains the candidate behaviour,
    ///     else "baseline output". No Ollama.
    /// </summary>
    private sealed class FakePlaybookEvalAgentRunner : IPlaybookEvalAgentRunner
    {
        public Task<string> RunAsync(IChatClient chatClient,
            string systemInstructions,
            IReadOnlyList<ChatMessage> inputTurns,
            CancellationToken cancellationToken = default)
        {
            var text = systemInstructions.Contains(CandidateBehavior, StringComparison.Ordinal)
                ? "candidate output"
                : "baseline output";
            return Task.FromResult(text);
        }
    }

    /// <summary>
    ///     Captures the candidate system prompt (the one containing the candidate behaviour) so a test can assert
    ///     composition order. No Ollama.
    /// </summary>
    private sealed class CapturingEvalAgentRunner : IPlaybookEvalAgentRunner
    {
        public string? CandidatePrompt { get; private set; }

        public Task<string> RunAsync(IChatClient chatClient,
            string systemInstructions,
            IReadOnlyList<ChatMessage> inputTurns,
            CancellationToken cancellationToken = default)
        {
            if (systemInstructions.Contains(CandidateBehavior, StringComparison.Ordinal))
            {
                CandidatePrompt = systemInstructions;
            }

            return Task.FromResult("output");
        }
    }

    /// <summary>Scripted judge: a predicate over (case, candidate text) decides pass; scoredBy is always "judge".</summary>
    private sealed class FakePlaybookEvalJudge(Func<GoldenConversationRecord, string, bool> pass) : IPlaybookEvalJudge
    {
        public Task<EvalScore> ScoreAsync(GoldenConversationRecord goldenCase,
            string candidateText,
            IChatClient nodeLocalClient,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new EvalScore(pass(goldenCase, candidateText), "judge"));
        }
    }
}
