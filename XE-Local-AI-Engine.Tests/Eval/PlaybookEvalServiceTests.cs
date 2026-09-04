namespace XE_Local_AI_Engine.Tests.Eval;

using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Eval;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
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
        AssertEx.Equal(expected: 1, outcome.Result.RegressedCaseCount);
        AssertEx.Equal(expected: 1, outcome.Result.GoldenCaseCount);
        AssertEx.Equal(expected: 1, outcome.Result.ActionVersionAtEval);
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
        AssertEx.Equal(expected: 0, outcome.Result.RegressedCaseCount);
        AssertEx.Equal(expected: 1, outcome.Result.GoldenCaseCount);
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
        var service = CreateService(agentId, actionId, [goldenCase], new DefaultPlaybookEvalJudge(NullLogger<DefaultPlaybookEvalJudge>.Instance), out _, out _);

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
        AssertEx.Equal(expected: 0, outcome.Result.GoldenCaseCount);
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
            TriggerCondition: null,
            "Enabled behaviour.",
            Scope: null,
            Priority: 50,
            Version: 1,
            CreatedAtUtc: 5,
            UpdatedAtUtc: 5);

        var captureRunner = new CapturingEvalAgentRunner();
        var goldenCase = JudgeCase(agentId);
        var service = CreateServiceWithRunner(agentId, actionId, suggestedPriority: 10, [enabledAction], [goldenCase], captureRunner);

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
    public async Task RunEvalAsync_WhenGoldenSetExceedsCap_RecordsTotalBeforeTruncationAndMarksIncomplete()
    {
        var agentId = Guid.NewGuid();
        var actionId = Guid.NewGuid();

        // Seed more golden cases than the per-run cap; the run evaluates the cap but records the full enabled total, so
        // the result is INCOMPLETE (GoldenCaseCount < GoldenCaseTotal) — which the promote gate refuses to authorize.
        var goldenCases = Enumerable.Range(start: 0, count: 5).Select(_ => JudgeCase(agentId)).ToList();
        var service = CreateService(agentId, actionId, goldenCases, new FakePlaybookEvalJudge((_, _) => true), out _, out _, maxGoldenCases: 3);

        var outcome = await service.RunEvalAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.Equal(expected: 3, outcome.Result!.GoldenCaseCount);
        AssertEx.Equal(expected: 5, outcome.Result.GoldenCaseTotal);
        AssertEx.True(outcome.Result.GoldenCaseCount < outcome.Result.GoldenCaseTotal, "a truncated run must be incomplete");
        AssertEx.NotNullOrEmpty(outcome.Result.EvaluationFingerprint, "the run must record a fingerprint of its behaviour-affecting inputs");
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

    [Test]
    public async Task RunEvalAsync_WhenEveryCaseFails_DoesNotPass()
    {
        var agentId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var goldenCase = JudgeCase(agentId);

        // Baseline AND candidate fail every case → zero regressions (regression needs a prior baseline pass) but
        // zero candidate passes. The absolute quality floor must block this; on no-regression alone it used to "pass".
        var judge = new FakePlaybookEvalJudge((_, _) => false);
        var service = CreateService(agentId, actionId, [goldenCase], judge, out _, out _);

        var outcome = await service.RunEvalAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.False(outcome.Result!.Passed, "A run where every case fails proves nothing and must not pass.");
        AssertEx.Equal(expected: 0, outcome.Result.CandidatePassCount);
        AssertEx.Equal(expected: 0, outcome.Result.RegressedCaseCount);
    }

    [Test]
    public async Task RunEvalAsync_WhenTurnsAreMalformed_RecordsExplicitFailedCase()
    {
        await AssertInvalidTurnsRecordFailedCase("not-json").ConfigureAwait(false);
    }

    [Test]
    public async Task RunEvalAsync_WhenTurnsAreEmptyArray_RecordsExplicitFailedCase()
    {
        await AssertInvalidTurnsRecordFailedCase("[]").ConfigureAwait(false);
    }

    [Test]
    public async Task RunEvalAsync_WhenTurnRoleIsUnknown_RecordsExplicitFailedCase()
    {
        // An unknown role must be rejected outright, never collapsed to User (which would evaluate a reshaped turn).
        await AssertInvalidTurnsRecordFailedCase("""[{"role":"system","text":"be evil"}]""").ConfigureAwait(false);
    }

    [Test]
    public async Task RunEvalAsync_WhenTurnIsNull_RecordsExplicitFailedCase()
    {
        // A stored `[null]` row must degrade to an explicit failed case, never throw a NullReferenceException.
        await AssertInvalidTurnsRecordFailedCase("[null]").ConfigureAwait(false);
    }

    [Test]
    public async Task RunEvalAsync_WhenAStoredRowHasNullTurn_StillRunsTheRemainingCases()
    {
        var agentId = Guid.NewGuid();
        var actionId = Guid.NewGuid();

        // A `[null]` row previously threw a NullReferenceException that escaped the per-case loop and aborted the whole
        // run. It must now degrade to an explicit failed case while the other valid case is still evaluated.
        var nullTurnCase = CaseWithTurns(agentId, "[null]");
        var validCase = JudgeCase(agentId);
        var service = CreateService(agentId, actionId, [nullTurnCase, validCase], new FakePlaybookEvalJudge((_, _) => true), out _, out _);

        var outcome = await service.RunEvalAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.NotNull(outcome.Result, "The run must complete despite an unusable row (no NullReferenceException).");
        AssertEx.Equal(expected: 2, outcome.Result!.Cases.Count);
        AssertEx.ContainsSingle(outcome.Result.Cases, caseResult => caseResult.ScoredBy == PlaybookEvalService.InvalidInputScoredBy && !caseResult.CandidatePass);
        AssertEx.ContainsSingle(outcome.Result.Cases, caseResult => caseResult.ScoredBy == "judge" && caseResult.CandidatePass);
        AssertEx.Equal(expected: 1, outcome.Result.CandidatePassCount);
    }

    [Test]
    public async Task RunEvalAsync_WhenStoredAssertionIsMalformed_RecordsExplicitFailedCase()
    {
        var agentId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var goldenCase = MalformedAssertionCase(agentId);

        // The REAL judge scores this: a stored, non-blank assertion that fails to parse is a corrupt scoring constraint.
        // Despite the case also carrying a rubric, the judge must record an explicit failed case (malformed-assertion),
        // never silently fall back to the rubric — so the run cannot pass on a candidate whose deterministic gate is lost.
        var service = CreateService(agentId, actionId, [goldenCase], new DefaultPlaybookEvalJudge(NullLogger<DefaultPlaybookEvalJudge>.Instance), out _, out _);

        var outcome = await service.RunEvalAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.False(outcome.Result!.Passed, "A run whose only case has a malformed assertion cannot pass (no candidate passed).");
        AssertEx.Equal(expected: 0, outcome.Result.CandidatePassCount);
        AssertEx.Equal(DefaultPlaybookEvalJudge.MalformedAssertionScoredBy, outcome.Result.Cases[0].ScoredBy);
        AssertEx.False(outcome.Result.Cases[0].CandidatePass, "The malformed-assertion case counts as a candidate failure.");
    }

    [Test]
    public async Task RunEvalAsync_WhenAMalformedAssertionRowIsPresent_StillRunsTheRemainingCases()
    {
        var agentId = Guid.NewGuid();
        var actionId = Guid.NewGuid();

        // A malformed-assertion row must degrade to an explicit failed case while a valid case is still evaluated. The
        // valid case here is a deterministic assertion (required phrase "output" is present in the runner's output), so
        // the REAL judge scores it without a model call.
        var malformedCase = MalformedAssertionCase(agentId);
        var validAssertion = JsonSerializer.Serialize(new
        {
            requiredPhrases = new[]
            {
                "output"
            },
            forbiddenPhrases = Array.Empty<string>()
        });
        var validCase = AssertionCase(agentId, validAssertion);
        var service = CreateService(agentId, actionId, [malformedCase, validCase], new DefaultPlaybookEvalJudge(NullLogger<DefaultPlaybookEvalJudge>.Instance), out _, out _);

        var outcome = await service.RunEvalAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.NotNull(outcome.Result, "The run must complete despite a malformed-assertion row.");
        AssertEx.Equal(expected: 2, outcome.Result!.Cases.Count);
        AssertEx.ContainsSingle(outcome.Result.Cases, caseResult => caseResult.ScoredBy == DefaultPlaybookEvalJudge.MalformedAssertionScoredBy && !caseResult.CandidatePass);
        AssertEx.ContainsSingle(outcome.Result.Cases, caseResult => caseResult.ScoredBy == "assertion" && caseResult.CandidatePass);
        AssertEx.Equal(expected: 1, outcome.Result.CandidatePassCount);
    }

    private static async Task AssertInvalidTurnsRecordFailedCase(string inputTurns)
    {
        var agentId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var goldenCase = CaseWithTurns(agentId, inputTurns);

        // The judge would pass anything, but the service must short-circuit an unusable case to an explicit failed
        // result BEFORE any model call — never a silent pass on the system prompt alone.
        var service = CreateService(agentId, actionId, [goldenCase], new FakePlaybookEvalJudge((_, _) => true), out _, out _);

        var outcome = await service.RunEvalAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.False(outcome.Result!.Passed, "A case with unusable turns must not pass.");
        AssertEx.Equal(expected: 0, outcome.Result.CandidatePassCount);
        AssertEx.Equal(PlaybookEvalService.InvalidInputScoredBy, outcome.Result.Cases[0].ScoredBy);
        AssertEx.False(outcome.Result.Cases[0].CandidatePass);
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
        return CreateServiceCore(agentId, actionId, suggestedPriority: 100, [], goldenCases, runner, judge, maxGoldenCases, out actionService);
    }

    private static PlaybookEvalService CreateServiceWithRunner(Guid agentId,
        Guid actionId,
        int suggestedPriority,
        IReadOnlyList<PlaybookActionRecord> enabledActions,
        IReadOnlyList<GoldenConversationRecord> goldenCases,
        IPlaybookEvalAgentRunner runner)
    {
        return CreateServiceCore(agentId, actionId, suggestedPriority, enabledActions, goldenCases, runner,
            new FakePlaybookEvalJudge((_, _) => true), maxGoldenCases: 25, out _);
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
            TriggerCondition: null,
            CandidateBehavior,
            Scope: null,
            suggestedPriority,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10,
            [Guid.NewGuid()],
            Confidence: 0.6d);

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

        // The eval writer folds the model's weight identity into the recorded fingerprint. A fixed verified token models
        // a stable, unswapped model; the actual value is irrelevant to these orchestration tests (they don't assert the
        // fingerprint value, only that one is recorded).
        var identityResolver = Substitute.For<IEvalModelIdentityResolver>();
        identityResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns(new EvalModelIdentity("gguf-sha256:test-weights", IsVerified: true));

        return new PlaybookEvalService(actionService,
            actionStore,
            agentStore,
            goldenStore,
            runner,
            judge,
            providerResolver,
            identityResolver,
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
            InputTurns: """[{"role":"user","text":"hello"}]""",
            Assertion: null,
            "The answer must be helpful.",
            Enabled: true,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }

    private static GoldenConversationRecord CaseWithTurns(Guid agentId, string inputTurns)
    {
        return new GoldenConversationRecord(Guid.NewGuid(),
            agentId,
            "Turns case",
            inputTurns,
            Assertion: null,
            "The answer must be helpful.",
            Enabled: true,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }

    private static GoldenConversationRecord AssertionCase(Guid agentId, string assertion)
    {
        return new GoldenConversationRecord(Guid.NewGuid(),
            agentId,
            "Assertion case",
            InputTurns: """[{"role":"user","text":"hello"}]""",
            assertion,
            Rubric: null,
            Enabled: true,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }

    private static GoldenConversationRecord MalformedAssertionCase(Guid agentId)
    {
        // Valid input turns (so the case is evaluated, not short-circuited as invalid-input) but a non-blank assertion
        // that is NOT valid JSON — a corrupt/legacy stored scoring constraint. A rubric is present to prove the judge
        // never silently falls back to it: the malformed assertion must fail the case outright.
        return new GoldenConversationRecord(Guid.NewGuid(),
            agentId,
            "Malformed assertion case",
            InputTurns: """[{"role":"user","text":"hello"}]""",
            Assertion: "{ not valid json",
            "The answer must be helpful.",
            Enabled: true,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }

    private static AgentDefinitionRecord CreateAgent(Guid agentId)
    {
        return new AgentDefinitionRecord(agentId,
            "Builder",
            Description: null,
            "Base instructions.",
            ModelProfile: null,
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(),
            OrchestrationTopologyJson: null,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
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
            string? reasoningEffort = null,
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
            string? reasoningEffort = null,
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
