namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The pairwise planner, its output contract and the fitter's refusals. The store is substituted here: what these
///     cover is the DECISIONS — which pairs exist, which verdict a swapped judging means, and when a fit must publish
///     no score at all — not the transactions behind them, which the persistence suite owns.
/// </summary>
public sealed class BenchmarkPairwiseTests
{
    private static readonly Guid ProjectId = new("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid RevisionId = new("bbbbbbbb-0000-0000-0000-000000000000");
    private static readonly Guid RunA = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RunB = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RunC = new("33333333-3333-3333-3333-333333333333");
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
        // P2 has one case per project, so this asserts the CONTRACT P3 widens: "which answer is better" is meaningless
        // across two different questions, and the grouping is what makes that unrepresentable rather than unlikely.
        BenchmarkPairwiseCandidate[] candidates =
        [
            new(RunA, null, "hash-one"),
            new(RunB, null, "hash-one"),
            new(RunC, null, "hash-two")
        ];

        var plan = BenchmarkPairwisePlanner.Plan(candidates, BenchmarkPairwisePolicy.MaximumRuns);

        AssertEx.Equal(expected: 1, plan.Slots.Count);
        AssertEx.True(plan.Slots.All(static slot => slot.RunAId != RunC && slot.RunBId != RunC), "A run answering another case is in another group.");
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
             .Returns(new BenchmarkJudgePolicyRevisionRecord(RevisionId, ProjectId, 1,
                 BenchmarkJudgeSerialization.SerializePolicy(PairwisePolicy() with
                 {
                     Mode = BenchmarkJudgePolicyModes.Pointwise
                 }),
                 PolicyHash, ExecutionKey, 1, 1, 7));
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
        var store = Substitute.For<IBenchmarkStore>();
        var capture = new Captured<BenchmarkPairwiseFitCommand>();
        published = capture;
        var (runA, runB) = RunA.CompareTo(RunB) < 0 ? (RunA, RunB) : (RunB, RunA);
        BenchmarkComparisonRecord[] comparisons =
        [
            Comparison(runA, runB, order: 0, "a", firstComparisonStatus,
                mismatchedKeyOnFirstComparison ? "other-runtime" : ExecutionKey, truncateFirstComparison),
            Comparison(runA, runB, order: 1, "a", BenchmarkJudgeAttemptStatus.Succeeded, ExecutionKey, false)
        ];
        store.GetCurrentJudgePolicyRevisionAsync(ProjectId, Arg.Any<CancellationToken>())
             .Returns(new BenchmarkJudgePolicyRevisionRecord(RevisionId, ProjectId, 1, BenchmarkJudgeSerialization.SerializePolicy(PairwisePolicy()),
                 PolicyHash, referenceExecutionKey, 1, 1, 7));
        store.GetPairwiseCohortAsync(ProjectId, Arg.Any<CancellationToken>())
             .Returns(new BenchmarkPairwiseCohortState(RevisionId, 1, 7, referenceExecutionKey, 1,
                 [new BenchmarkPairwiseCandidate(runA, null, string.Empty), new BenchmarkPairwiseCandidate(runB, null, string.Empty)],
                 comparisons));
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
        new(Guid.NewGuid(), ProjectId, RevisionId, 1, null, string.Empty, runA, runB, order, 1, order + 1, status,
            status == BenchmarkJudgeAttemptStatus.Succeeded ? verdict : null, truncated, false, executionKey, null, null, 1, 2, 3, 1);

    private static BenchmarkPairwiseCandidate[] Candidates(params Guid[] runs) =>
        [.. runs.Select(static run => new BenchmarkPairwiseCandidate(run, null, string.Empty))];

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
