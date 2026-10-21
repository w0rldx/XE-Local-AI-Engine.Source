namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The pairwise planner, its output contract and the fitter's refusals. The store is substituted here: what these
///     cover is the DECISIONS — which pairs exist, which verdict a swapped judging means, and when a fit must publish
///     no score at all — not the transactions behind them, which the persistence suite owns.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class BenchmarkPairwiseTests
{
    private static readonly Guid ProjectId = new("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid RevisionId = new("bbbbbbbb-0000-0000-0000-000000000000");
    private static readonly Guid RunA = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RunB = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RunC = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid RunD = new("77777777-7777-7777-7777-777777777777");
    private static readonly Guid ItemZero = new("cccccccc-0000-0000-0000-000000000000");
    private static readonly Guid ItemOne = new("dddddddd-0000-0000-0000-000000000000");
    private const string PolicyHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string ExecutionKey = "exec-v1";
    private static readonly JsonSerializerOptions ScoreOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public void Plan_FormsEveryUnorderedPairInCanonicalOrder()
    {
        var plan = BenchmarkPairwisePlanner.Plan(Candidates(RunA, RunB, RunC), BenchmarkPairwisePolicy.MaximumRuns);

        AssertEx.Equal(expected: 3, plan.Slots.Count);
        AssertEx.True(plan.Slots.All(static slot => slot.RunAId.CompareTo(slot.RunBId) < 0), "The planner must already agree with the DB's canonical-order CHECK.");
        AssertEx.Empty(plan.CappedRunIds);
    }

    [Test]
    public void Plan_AddingOneRunToAGroupOfN_AddsExactlyNNewPairs()
    {
        var before = BenchmarkPairwisePlanner.Plan(Candidates(RunA, RunB, RunC), BenchmarkPairwisePolicy.MaximumRuns);
        var after = BenchmarkPairwisePlanner.Plan(Candidates(RunA, RunB, RunC, Guid.NewGuid()), BenchmarkPairwisePolicy.MaximumRuns);

        // 3 new pairs = 6 new comparisons, not the whole tournament again. Enqueue is incremental by construction.
        AssertEx.Equal(before.Slots.Count + 3, after.Slots.Count);
    }

    [Test]
    public void Plan_PastTheCap_PairsNothingBeyondItAndNamesTheRunsItLeftOut()
    {
        var candidates = Candidates([.. Enumerable.Range(0, BenchmarkPairwisePolicy.MaximumRuns + 3).Select(static _ => Guid.NewGuid())]);

        var plan = BenchmarkPairwisePlanner.Plan(candidates, BenchmarkPairwisePolicy.MaximumRuns);

        AssertEx.Equal(BenchmarkPairwisePolicy.MaximumRuns, plan.PairedRunIds.Count);
        AssertEx.Equal(expected: 3, plan.CappedRunIds.Count, "The excess runs are named so the ranking can say pairwise-cap rather than going quiet.");
        AssertEx.Equal(BenchmarkPairwisePolicy.MaximumRuns * (BenchmarkPairwisePolicy.MaximumRuns - 1) / 2, plan.Slots.Count);
    }

    [Test]
    public void Plan_RunsAnsweringDifferentTaskCases_AreNeverPaired()
    {
        // A suite can carry multiple task cases, but "which answer is better" is meaningless across two different
        // questions. Grouping by case identity makes such a comparison unrepresentable rather than merely unlikely.
        BenchmarkPairwiseCandidate[] candidates =
        [
            new()
            {
                RunId = RunA,
                TaskCaseId = null,
                TaskInputHash = "hash-one",
                ModelName = "model-a",
                AgentDefinitionId = Guid.Empty
            },
            new()
            {
                RunId = RunB,
                TaskCaseId = null,
                TaskInputHash = "hash-one",
                ModelName = "model-b",
                AgentDefinitionId = Guid.Empty
            },
            new()
            {
                RunId = RunC,
                TaskCaseId = null,
                TaskInputHash = "hash-two",
                ModelName = "model-c",
                AgentDefinitionId = Guid.Empty
            }
        ];

        var plan = BenchmarkPairwisePlanner.Plan(candidates, BenchmarkPairwisePolicy.MaximumRuns);

        AssertEx.Equal(expected: 1, plan.Slots.Count);
        AssertEx.True(plan.Slots.All(static slot => slot.RunAId != RunC && slot.RunBId != RunC), "A run answering another case is in another group.");
    }

    [Test]
    public void Plan_TwoAgentVariantsOfOneModel_ArePairedButOneVariantsRepeatsAreNot()
    {
        var projectAgent = new Guid("88888888-0000-0000-0000-000000000000");
        var variant = new Guid("99999999-0000-0000-0000-000000000000");
        BenchmarkPairwiseCandidate[] candidates =
        [
            Candidate(RunA, ItemZero, "model-9b", projectAgent),
            Candidate(RunB, ItemZero, "model-9b", variant),
            Candidate(RunC, ItemZero, "model-9b", variant)
        ];

        var plan = BenchmarkPairwisePlanner.Plan(candidates, BenchmarkPairwisePolicy.MaximumRuns);

        // The skip key is (model, agent): A pairs with each variant run, and the variant's two repeats never pair.
        AssertEx.Equal(expected: 2, plan.Slots.Count);
        AssertEx.True(plan.Slots.All(static slot => slot.RunAId == RunA || slot.RunBId == RunA), "Every pair must cross the two agent variants.");
    }

    [Test]
    public void Plan_TwoItemsByTwoModels_PairsOnlyDifferentModelsOnTheSameItem()
    {
        // R7-02: 2 items x 2 models used to produce 6 pairs, including one model's item 1 against its own item 0.
        var item0Repeat = new Guid("44444444-4444-4444-4444-444444444444");
        var item1Large = new Guid("55555555-5555-5555-5555-555555555555");
        var item1Small = new Guid("66666666-6666-6666-6666-666666666666");
        BenchmarkPairwiseCandidate[] candidates =
        [
            Candidate(RunA, ItemZero, "cell-27b"),
            Candidate(RunB, ItemZero, "cell-9b"),
            Candidate(item0Repeat, ItemZero, "cell-27b"),
            Candidate(item1Large, ItemOne, "cell-27b"),
            Candidate(item1Small, ItemOne, "cell-9b")
        ];

        var plan = BenchmarkPairwisePlanner.Plan(candidates, BenchmarkPairwisePolicy.MaximumRuns);

        var pairs = plan.Slots.Select(static slot => string.Join('|', new[]
        {
            slot.RunAId,
            slot.RunBId
        }.Order())).Order().ToArray();
        string[] expected =
        [
            .. new[]
            {
                new[]
                {
                    RunA,
                    RunB
                },
                new[]
                {
                    item0Repeat,
                    RunB
                },
                new[]
                {
                    item1Large,
                    item1Small
                }
            }.Select(static pair => string.Join('|', pair.Order())).Order()
        ];
        AssertEx.Equal(string.Join(", ", expected), string.Join(", ", pairs),
            "Only different models answering the same item are compared; a repeat of the same model is not.");
    }

    [Test]
    public async Task Fitter_TwoItems_ScoresEveryRunWithinItsOwnItem()
    {
        var store = StubCohort(out var published, TwoItemCandidates(), TwoItemComparisons(withStaleCrossItemPair: false));

        AssertEx.True(await Fitter(store).TryPublishAsync(ProjectId, CancellationToken.None));

        var scores = Entries(published.Value);
        AssertEx.Equal(expected: 4, scores.Count(static entry => entry.Score is not null),
            "Each item is fitted on its own; one fit over both would score only the largest component.");
        AssertEx.True(scores.Single(entry => entry.RunId == RunA).Score > scores.Single(entry => entry.RunId == RunB).Score);
        AssertEx.True(scores.Single(entry => entry.RunId == RunC).Score > scores.Single(entry => entry.RunId == RunD).Score);
    }

    /// <summary>One model with repeats plans no pair; the cohort must not read pending forever behind an older fit.</summary>
    [Test]
    public async Task Fitter_SeveralRunsOfOneCell_PublishesARefusalInsteadOfStayingPending()
    {
        BenchmarkPairwiseCandidate[] candidates =
        [
            Candidate(RunA, ItemZero, "cell-27b"),
            Candidate(RunB, ItemZero, "cell-27b"),
            Candidate(RunC, ItemZero, "cell-27b")
        ];
        var store = StubCohort(out var published, candidates, [], referenceExecutionKey: null);

        AssertEx.True(await Fitter(store).TryPublishAsync(ProjectId, CancellationToken.None));

        var scores = Entries(published.Value);
        AssertEx.Equal(expected: 3, scores.Length);
        AssertEx.True(scores.All(static entry => entry.Score is null && entry.Reason == BenchmarkRunJudgeStates.ReasonPairwiseInsufficient),
            "Every run of the single cell reads why it has no pairwise score.");
    }

    [Test]
    public async Task Fitter_StaleCrossItemComparison_IsNotFitted()
    {
        // Comparisons stored before pairing respected task items stay in the cohort; the fit must ignore them.
        var store = StubCohort(out var published, TwoItemCandidates(), TwoItemComparisons(withStaleCrossItemPair: true));

        AssertEx.True(await Fitter(store).TryPublishAsync(ProjectId, CancellationToken.None));

        using var fitted = JsonDocument.Parse(published.Value.FittedSetJson);
        AssertEx.Equal(expected: 4, fitted.RootElement.GetArrayLength(), "Only the two within-item pairs, both orders, are fitted.");
    }

    [Test]
    public void Parse_AcceptsTheSchemaAndRejectsEverythingElse()
    {
        var parsed = BenchmarkPairwiseResultParser.Parse("{\"schemaVersion\":1,\"verdict\":\"b\",\"rationale\":\"B answered the question.\"}");

        AssertEx.Equal("b", parsed.Verdict);
        AssertEx.Equal("B answered the question.", parsed.Rationale);
        _ = AssertEx.Throws<BenchmarkExecutionException>(static () => BenchmarkPairwiseResultParser.Parse("{\"schemaVersion\":1,\"verdict\":\"A\",\"rationale\":\"x\"}"));
        _ = AssertEx.Throws<BenchmarkExecutionException>(static () => BenchmarkPairwiseResultParser.Parse("{\"schemaVersion\":2,\"verdict\":\"a\",\"rationale\":\"x\"}"));
        _ = AssertEx.Throws<BenchmarkExecutionException>(static () => BenchmarkPairwiseResultParser.Parse("{\"verdict\":\"a\"}"));
        _ = AssertEx.Throws<BenchmarkExecutionException>(static () => BenchmarkPairwiseResultParser.Parse(""));
    }

    [Test]
    public void ToCanonicalVerdict_SwappedOrder_MapsTheVerdictBackToThePair()
    {
        // Order 1 shows the canonical B first, so the judge saying "a" means B won. Getting this backwards would
        // invert exactly half the verdicts of every cohort — and the position swap is what makes it half.
        AssertEx.Equal("a", BenchmarkPairwiseResultParser.ToCanonicalVerdict("a", order: 0));
        AssertEx.Equal("b", BenchmarkPairwiseResultParser.ToCanonicalVerdict("a", order: 1));
        AssertEx.Equal("a", BenchmarkPairwiseResultParser.ToCanonicalVerdict("b", order: 1));
        AssertEx.Equal("tie", BenchmarkPairwiseResultParser.ToCanonicalVerdict("tie", order: 1));
    }

    [Test]
    public void ResponseFormatSchema_CarriesNoLengthBounds()
    {
        // llama.cpp compiles the response format into GBNF and its sampler initialization breaks on length bounds.
        // The documentation copy keeps them, because the parser still enforces them.
        AssertEx.False(BenchmarkPairwiseOutputSchemaV1.ResponseFormatJson.Contains("maxLength", StringComparison.Ordinal));
        AssertEx.False(BenchmarkPairwiseOutputSchemaV1.ResponseFormatJson.Contains("minLength", StringComparison.Ordinal));
        AssertEx.True(BenchmarkPairwiseOutputSchemaV1.Json.Contains("maxLength", StringComparison.Ordinal));
    }

    [Test]
    public async Task Fitter_CohortWithNoPromotedExecutionKey_PublishesNoScoreAndSaysWhy()
    {
        var store = StubStore(out var published, referenceExecutionKey: null);

        AssertEx.True(await Fitter(store).TryPublishAsync(ProjectId, CancellationToken.None));

        AssertEx.True(Entries(published.Value).All(static entry => entry.Score is null), "A refused fit publishes no score at all.");
        AssertEx.True(Entries(published.Value).All(static entry => entry.Reason == BenchmarkRunJudgeStates.ReasonPairwiseExecutionIdentityIncomplete));
    }

    [Test]
    public async Task Fitter_ComparisonJudgedByAnotherRuntime_RefusesTheWholeFit()
    {
        var store = StubStore(out var published, mismatchedKeyOnFirstComparison: true);

        AssertEx.True(await Fitter(store).TryPublishAsync(ProjectId, CancellationToken.None));

        // No partial fit over the matching subset: dropping comparisons changes the graph, can disconnect it, and
        // publishes a number over a set the operator never chose.
        AssertEx.True(Entries(published.Value).All(static entry => entry.Reason == BenchmarkRunJudgeStates.ReasonPairwiseExecutionMismatch));
        AssertEx.True(Entries(published.Value).All(static entry => entry.Score is null));
    }

    [Test]
    public async Task Fitter_MoreThanAFifthOfVerdictsHadATruncatedSide_RefusesToAggregate()
    {
        var store = StubStore(out var published, truncateFirstComparison: true);

        AssertEx.True(await Fitter(store).TryPublishAsync(ProjectId, CancellationToken.None));

        AssertEx.True(Entries(published.Value).All(static entry => entry.Reason == BenchmarkRunJudgeStates.ReasonPairwiseInsufficient),
            "Half the window each means a long answer is cut harder here than pointwise; a cohort full of cuts is a biased one.");
    }

    [Test]
    public async Task Fitter_CompleteCohort_PublishesStrengthsAndStampsTheSetVersionItFitAt()
    {
        var store = StubStore(out var published);

        AssertEx.True(await Fitter(store).TryPublishAsync(ProjectId, CancellationToken.None));

        var command = published.Value;
        AssertEx.Equal(expected: 7, command.ComparisonSetVersion, "The fit stamps the set version it was fit at, and staleness is that integer.");
        AssertEx.Equal(ExecutionKey, command.JudgeExecutionKey);
        AssertEx.True(command.FitKey.StartsWith("v1:", StringComparison.Ordinal));
        var scores = Entries(command);
        AssertEx.Equal(expected: 2, scores.Length);
        AssertEx.True(scores.All(static entry => entry.Score is >= 0 and <= 100));
        AssertEx.True(scores.Single(entry => entry.RunId == RunA).Score > scores.Single(entry => entry.RunId == RunB).Score,
            "The run that won both presentation orders must come out ahead.");
    }

    [Test]
    [Arguments(ExecutionKey)]
    [Arguments(null)]
    public async Task Fitter_PublishesExactlyOneNonNullEntryPerCandidateRun(string? referenceExecutionKey)
    {
        // The readers tolerate a null element or a run named twice; this pins that the only writer produces neither,
        // on the fitted path and on a whole-fit refusal alike.
        var store = StubStore(out var published, referenceExecutionKey);

        AssertEx.True(await Fitter(store).TryPublishAsync(ProjectId, CancellationToken.None));

        var stored = JsonSerializer.Deserialize<BenchmarkPairwiseScoreEntry?[]>(published.Value.ScoresJson, ScoreOptions) ?? [];
        AssertEx.True(stored.All(static entry => entry is not null), "The fitter never writes a null entry.");
        AssertEx.Equal(expected: 2, stored.Length);
        AssertEx.Equal(expected: 2, stored.Select(static entry => entry!.RunId).Distinct().Count(), "The fitter never names a run twice.");
        AssertEx.True(stored.Select(static entry => entry!.RunId).ToHashSet().SetEquals([RunA, RunB]));
    }

    [Test]
    public async Task Fitter_CohortStillJudging_PublishesNothing()
    {
        var store = StubStore(out _, firstComparisonStatus: BenchmarkJudgeAttemptStatus.Queued);

        AssertEx.False(await Fitter(store).TryPublishAsync(ProjectId, CancellationToken.None));
        await store.DidNotReceive().PublishPairwiseFitAsync(Arg.Any<BenchmarkPairwiseFitCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnsurePairs_ForAProjectBackOnPointwise_PlansNothing()
    {
        // The other half of the mode switch: activation stops seeding pointwise attempts for a pairwise project, and
        // the planner must stay a no-op for a pointwise one — otherwise switching back would leave comparisons queued
        // against a revision whose ranking never reads them.
        var store = StubStore(out _);
        store.GetCurrentJudgePolicyRevisionAsync(ProjectId, Arg.Any<CancellationToken>())
             .Returns(new BenchmarkJudgePolicyRevisionRecord
             {
                 Id = RevisionId,
                 ProjectId = ProjectId,
                 Revision = 1,
                 PolicyJson = BenchmarkJudgeSerialization.SerializePolicy(PairwisePolicy() with
                 {
                     Mode = BenchmarkJudgePolicyModes.Pointwise
                 }),
                 PolicyHash = PolicyHash,
                 ReferenceExecutionKey = ExecutionKey,
                 CohortGeneration = 1,
                 CreatedAtUtc = 1,
                 ComparisonSetVersion = 7
             });
        var runtimes = Substitute.For<IBenchmarkJudgeRuntimeResolver>();
        var planner = new BenchmarkPairwisePlanner(store, runtimes, Fitter(store), Substitute.For<IBenchmarkQueueSignal>(),
            NullLogger<BenchmarkPairwisePlanner>.Instance);

        AssertEx.Equal(expected: 0, await planner.EnsurePairsAsync(ProjectId, CancellationToken.None));

        await store.DidNotReceiveWithAnyArgs()
                   .EnsureComparisonsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<BenchmarkPairwiseSlot>>(), Arg.Any<ReadOnlyMemory<byte>?>(),
                       Arg.Any<BenchmarkRunLaunchIntent?>(), Arg.Any<CancellationToken>());
        await runtimes.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
    }

    private static BenchmarkPairwiseFitter Fitter(IBenchmarkStore store) =>
        new(store, NullLogger<BenchmarkPairwiseFitter>.Instance);

    private static BenchmarkPairwiseScoreEntry[] Entries(BenchmarkPairwiseFitCommand command) =>
        JsonSerializer.Deserialize<BenchmarkPairwiseScoreEntry[]>(command.ScoresJson, ScoreOptions) ?? [];

    /// <summary>A two-run cohort whose four comparisons all succeeded, with the knobs each refusal test needs.</summary>
    private static IBenchmarkStore StubStore(out Captured<BenchmarkPairwiseFitCommand> published,
        string? referenceExecutionKey = ExecutionKey,
        bool mismatchedKeyOnFirstComparison = false,
        bool truncateFirstComparison = false,
        BenchmarkJudgeAttemptStatus firstComparisonStatus = BenchmarkJudgeAttemptStatus.Succeeded)
    {
        var (runA, runB) = RunA.CompareTo(RunB) < 0 ? (RunA, RunB) : (RunB, RunA);
        BenchmarkComparisonRecord[] comparisons =
        [
            Comparison(runA, runB, order: 0, "a", firstComparisonStatus,
                mismatchedKeyOnFirstComparison ? "other-runtime" : ExecutionKey, truncateFirstComparison),
            Comparison(runA, runB, order: 1, "a", BenchmarkJudgeAttemptStatus.Succeeded, ExecutionKey, false)
        ];
        return StubCohort(out published, Candidates(runA, runB), comparisons, referenceExecutionKey);
    }

    private static IBenchmarkStore StubCohort(out Captured<BenchmarkPairwiseFitCommand> published,
        BenchmarkPairwiseCandidate[] candidates,
        BenchmarkComparisonRecord[] comparisons,
        string? referenceExecutionKey = ExecutionKey)
    {
        var store = Substitute.For<IBenchmarkStore>();
        var capture = new Captured<BenchmarkPairwiseFitCommand>();
        published = capture;
        store.GetCurrentJudgePolicyRevisionAsync(ProjectId, Arg.Any<CancellationToken>())
             .Returns(new BenchmarkJudgePolicyRevisionRecord
             {
                 Id = RevisionId,
                 ProjectId = ProjectId,
                 Revision = 1,
                 PolicyJson = BenchmarkJudgeSerialization.SerializePolicy(PairwisePolicy()),
                 PolicyHash = PolicyHash,
                 ReferenceExecutionKey = referenceExecutionKey,
                 CohortGeneration = 1,
                 CreatedAtUtc = 1,
                 ComparisonSetVersion = 7
             });
        store.GetPairwiseCohortAsync(ProjectId, Arg.Any<CancellationToken>())
             .Returns(new BenchmarkPairwiseCohortState
             {
                 PolicyRevisionId = RevisionId,
                 CohortGeneration = 1,
                 ComparisonSetVersion = 7,
                 ReferenceExecutionKey = referenceExecutionKey,
                 ProjectVersion = 1,
                 Candidates = candidates,
                 Comparisons = comparisons
             });
        store.GetActivePairwiseFitAsync(ProjectId, Arg.Any<CancellationToken>()).Returns((BenchmarkPairwiseFitRecord?)null);
        store.PublishPairwiseFitAsync(Arg.Do<BenchmarkPairwiseFitCommand>(command => capture.Value = command), Arg.Any<CancellationToken>())
             .Returns(true);
        return store;
    }

    private static BenchmarkComparisonRecord Comparison(Guid runA,
        Guid runB,
        int order,
        string verdict,
        BenchmarkJudgeAttemptStatus status,
        string? executionKey,
        bool truncated) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProjectId = ProjectId,
            PolicyRevisionId = RevisionId,
            CohortGeneration = 1,
            TaskCaseId = null,
            TaskInputHash = string.Empty,
            RunAId = runA,
            RunBId = runB,
            Order = order,
            AttemptSequence = 1,
            Sequence = order + 1,
            Status = status,
            Verdict = status == BenchmarkJudgeAttemptStatus.Succeeded ? verdict : null,
            AnswerATruncated = truncated,
            AnswerBTruncated = false,
            JudgeExecutionKey = executionKey,
            ErrorMessage = null,
            JudgeRuntimeJson = null,
            EnqueuedAtUtc = 1,
            StartedAtUtc = 2,
            CompletedAtUtc = 3,
            Version = 1
        };

    private static BenchmarkPairwiseCandidate Candidate(Guid run, Guid item, string cell, Guid agent = default) =>
        new()
        {
            RunId = run,
            TaskCaseId = item,
            TaskInputHash = "hash-" + item,
            ModelName = cell,
            AgentDefinitionId = agent
        };

    /// <summary>Item 0: RunA (27B) and RunB (9B); item 1: RunC (27B) and RunD (9B).</summary>
    private static BenchmarkPairwiseCandidate[] TwoItemCandidates() =>
    [
        Candidate(RunA, ItemZero, "cell-27b"),
        Candidate(RunB, ItemZero, "cell-9b"),
        Candidate(RunC, ItemOne, "cell-27b"),
        Candidate(RunD, ItemOne, "cell-9b")
    ];

    /// <summary>The 27B wins both orders on both items; the stale pair is the 27B's item 0 against its own item 1.</summary>
    private static BenchmarkComparisonRecord[] TwoItemComparisons(bool withStaleCrossItemPair)
    {
        List<BenchmarkComparisonRecord> comparisons =
        [
            Comparison(RunA, RunB, order: 0, "a", BenchmarkJudgeAttemptStatus.Succeeded, ExecutionKey, false),
            Comparison(RunA, RunB, order: 1, "a", BenchmarkJudgeAttemptStatus.Succeeded, ExecutionKey, false),
            Comparison(RunC, RunD, order: 0, "a", BenchmarkJudgeAttemptStatus.Succeeded, ExecutionKey, false),
            Comparison(RunC, RunD, order: 1, "a", BenchmarkJudgeAttemptStatus.Succeeded, ExecutionKey, false)
        ];
        if (withStaleCrossItemPair)
        {
            comparisons.Add(Comparison(RunA, RunC, order: 0, "b", BenchmarkJudgeAttemptStatus.Succeeded, ExecutionKey, false));
            comparisons.Add(Comparison(RunA, RunC, order: 1, "b", BenchmarkJudgeAttemptStatus.Succeeded, ExecutionKey, false));
        }

        return [.. comparisons];
    }

    private static BenchmarkPairwiseCandidate[] Candidates(params Guid[] runs) =>
    [
        .. runs.Select(static run => new BenchmarkPairwiseCandidate
        {
            RunId = run,
            TaskCaseId = null,
            TaskInputHash = string.Empty,
            ModelName = "model-" + run,
            AgentDefinitionId = Guid.Empty
        })
    ];

    private static BenchmarkJudgePolicyV1 PairwisePolicy() =>
        new(new BenchmarkJudgePolicyModelV1("judge.gguf", "v1:" + new string('c', 64), ["v1:" + new string('b', 64)]),
            4096,
            BenchmarkJudgePolicyVersions.PromptVersion,
            BenchmarkJudgePolicyVersions.OutputSchemaVersion,
            BenchmarkJudgePolicySamplingV1.FromSnapshot(BenchmarkFrozenPolicies.DeterministicSampling()),
            new BenchmarkJudgeRubricV1(BenchmarkJudgePolicyVersions.RubricVersion,
            [
                new BenchmarkJudgeRubricCriterionV1("correctness", "Correctness", "Is the answer right?", 40)
            ]),
            ReferenceAnswer: null,
            BenchmarkJudgePolicyModes.Pairwise);

    private sealed class Captured<T>
    {
        public T Value { get; set; } = default!;
    }
}
