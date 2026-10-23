namespace XE_Local_AI_Engine.Tests.Benchmarks;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The application half of the review: scores fit the CURRENT rubric, the overall is the rubric-weighted one, a human never overrules a verifier, and only
///     a judging that succeeded under the current revision can be reviewed against.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class BenchmarkReviewServiceTests
{
    private static readonly Guid ProjectId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RevisionId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AttemptId = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid RunId = new("44444444-4444-4444-4444-444444444444");

    /// <summary>Two reviewer-scored criteria weighted 3:1 and one exact check worth 1, which the stored judging passed.</summary>
    private static readonly BenchmarkJudgeRubricV1 Rubric = new(BenchmarkJudgePolicyVersions.RubricVersion,
    [
        new BenchmarkJudgeRubricCriterionV1("accuracy", "Accuracy", "Is the answer right?", 3),
        new BenchmarkJudgeRubricCriterionV1("clarity", "Clarity", "Is it clear?", 1),
        new BenchmarkJudgeRubricCriterionV1("exact_answer", "Exact", "The answer text is exact.", 1, BenchmarkJudgeCriterionKinds.Exact, """{"expected":"answer"}""")
    ]);

    /// <summary>The code-benchmark shape: one verifier, no criterion for a human to score.</summary>
    private static readonly BenchmarkJudgeRubricV1 VerifierOnlyRubric = new(BenchmarkJudgePolicyVersions.RubricVersion,
    [
        new BenchmarkJudgeRubricCriterionV1("exact_answer", "Exact", "The answer text is exact.", 1, BenchmarkJudgeCriterionKinds.Exact, """{"expected":"answer"}""")
    ]);

    [Test]
    public async Task Submit_WhenCriteriaMissingOne_Rejects()
    {
        var context = new ServiceContext();

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => context.Service.SubmitAsync(Approve(Score("accuracy", 8))));

        _ = context.Store.DidNotReceive().SubmitReviewAsync(Arg.Any<BenchmarkReviewCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Submit_WhenRejectedWithPartialCriteria_Accepts()
    {
        var context = new ServiceContext();

        _ = await context.Service.SubmitAsync(Approve(Score("accuracy", 2)) with
        {
            State = BenchmarkReviewStates.Rejected
        });
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => context.Service.SubmitAsync(Approve(Score("accuracy", 11)) with
        {
            State = BenchmarkReviewStates.Rejected
        }));

        var submitted = AssertEx.NotNull(context.Submitted);
        AssertEx.Null(submitted.Score, "A rejection carries no overall.");
        var criteria = AssertEx.NotNull(submitted.Criteria);
        AssertEx.Equal(expected: 2, criteria.Single(static score => score.CriterionId == "accuracy").Score,
            "What was sent is kept; the unscored llm criterion is simply absent.");
        AssertEx.False(criteria.Any(static score => score.CriterionId == "clarity"));
        _ = context.Store.Received(1).SubmitReviewAsync(Arg.Any<BenchmarkReviewCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Submit_WhenRubricHasOnlyVerifiableCriteria_ApprovesOnTheVerifierScores()
    {
        var context = new ServiceContext(rubric: VerifierOnlyRubric);

        _ = await context.Service.SubmitAsync(Approve());

        var submitted = AssertEx.NotNull(context.Submitted);
        AssertEx.Equal<int?>(100, submitted.Score, "Nothing is reviewer-editable, so the approval stands on the verifier's pass.");
        AssertEx.Equal(expected: 10, AssertEx.NotNull(submitted.Criteria).Single().Score);
    }

    [Test]
    public async Task Submit_WhenRubricHasOnlyVerifiableCriteria_RejectsAValueDifferingFromTheVerifier()
    {
        var context = new ServiceContext(rubric: VerifierOnlyRubric);

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => context.Service.SubmitAsync(Approve(Score("exact_answer", 3))));

        _ = context.Store.DidNotReceive().SubmitReviewAsync(Arg.Any<BenchmarkReviewCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Submit_ComputesOverallWithTheRubricWeights()
    {
        var context = new ServiceContext();

        _ = await context.Service.SubmitAsync(Approve(Score("accuracy", 8), Score("clarity", 4)) with
        {
            Score = 1
        });

        var submitted = AssertEx.NotNull(context.Submitted);
        AssertEx.Equal<int?>(76, submitted.Score, "(3·80 + 1·40 + 1·100) / 5: the rubric's weights decide, not the caller's overall.");
        var criteria = AssertEx.NotNull(submitted.Criteria);
        AssertEx.Equal(expected: 3, criteria.Count, "The stored decision carries the whole rubric, the verifier's criterion included.");
        AssertEx.Equal(expected: 10, criteria.Single(static score => score.CriterionId == "exact_answer").Score);
    }

    [Test]
    public async Task Submit_StampsTheCurrentRevision()
    {
        var context = new ServiceContext();

        _ = await context.Service.SubmitAsync(Approve(Score("accuracy", 8), Score("clarity", 4)) with
        {
            PolicyRevisionId = Guid.NewGuid()
        });

        AssertEx.Equal<Guid?>(RevisionId, AssertEx.NotNull(context.Submitted).PolicyRevisionId, "The revision is the service's to stamp, never the caller's.");
    }

    [Test]
    public async Task Submit_WhenProjectHasNoPolicy_AcceptsOverallOnly()
    {
        var context = new ServiceContext(withPolicy: false);

        _ = await context.Service.SubmitAsync(Approve() with
        {
            Score = 55
        });
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => context.Service.SubmitAsync(Approve(Score("accuracy", 8)) with
        {
            Score = 55
        }));

        var submitted = AssertEx.NotNull(context.Submitted);
        AssertEx.Equal<int?>(55, submitted.Score, "Without a rubric the reviewer's overall is the score.");
        AssertEx.Null(submitted.PolicyRevisionId);
        _ = context.Store.Received(1).SubmitReviewAsync(Arg.Any<BenchmarkReviewCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Submit_WhenVerifiableCriterionDiffersFromVerifier_Rejects()
    {
        var context = new ServiceContext();

        var refusal = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            context.Service.SubmitAsync(Approve(Score("accuracy", 8), Score("clarity", 4), Score("exact_answer", 0))));

        AssertEx.True(refusal.Message.Contains("exact_answer", StringComparison.Ordinal), "The refusal names the criterion the verifier decided.");
        _ = context.Store.DidNotReceive().SubmitReviewAsync(Arg.Any<BenchmarkReviewCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Submit_WhenVerifiableCriterionMatches_Accepts()
    {
        var context = new ServiceContext();

        _ = await context.Service.SubmitAsync(Approve(Score("accuracy", 8), Score("clarity", 4), Score("exact_answer", 10)));

        AssertEx.Equal<int?>(76, AssertEx.NotNull(context.Submitted).Score, "Repeating the verifier's own value is the read-only field echoed back.");
    }

    [Test]
    public async Task Submit_WhenJudgeAttemptNotSucceeded_ThrowsNotReviewable()
    {
        var failed = new ServiceContext(judgeState: BenchmarkRunJudgeStates.Failed);
        var outdated = new ServiceContext(judgeRevisionId: Guid.NewGuid());

        var notSucceeded = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => failed.Service.SubmitAsync(Approve(Score("accuracy", 8), Score("clarity", 4))));
        var underOldRubric = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() =>
            outdated.Service.SubmitAsync(Approve(Score("accuracy", 8), Score("clarity", 4))));

        AssertEx.Equal("NotReviewable", notSucceeded.Code, "The operator re-judges first.");
        AssertEx.Equal("NotReviewable", underOldRubric.Code, "A judging under a superseded revision verified a rubric the reviewer is no longer scoring.");
        _ = failed.Store.DidNotReceive().SubmitReviewAsync(Arg.Any<BenchmarkReviewCommand>(), Arg.Any<CancellationToken>());
    }

    private static BenchmarkReviewCriterionScore Score(string criterionId, int score) =>
        new()
        {
            CriterionId = criterionId,
            Score = score
        };

    private static BenchmarkReviewCommand Approve(params BenchmarkReviewCriterionScore[] criteria) =>
        new()
        {
            RunId = RunId,
            ExpectedRunVersion = 3,
            State = BenchmarkReviewStates.Approved,
            Criteria = criteria
        };

    private sealed class ServiceContext
    {
        public ServiceContext(bool withPolicy = true,
            string judgeState = BenchmarkRunJudgeStates.Succeeded,
            Guid? judgeRevisionId = null,
            BenchmarkJudgeRubricV1? rubric = null)
        {
            var run = Run(withPolicy, judgeState, judgeRevisionId ?? RevisionId);
            Store.GetRunAsync(RunId, Arg.Any<CancellationToken>()).Returns(run);
            Store.GetCurrentJudgePolicyRevisionAsync(ProjectId, Arg.Any<CancellationToken>())
                 .Returns(withPolicy
                     ? new BenchmarkJudgePolicyRevisionRecord
                     {
                         Id = RevisionId,
                         ProjectId = ProjectId,
                         Revision = 1,
                         PolicyJson = BenchmarkJudgeSerialization.SerializePolicy(BenchmarkJudgeExecutorTests.Policy(rubric: rubric ?? Rubric)),
                         PolicyHash = new string('a', count: 64),
                         ReferenceExecutionKey = null,
                         CohortGeneration = 1,
                         CreatedAtUtc = 1
                     }
                     : null);
            Store.GetJudgeAttemptAsync(AttemptId, Arg.Any<CancellationToken>()).Returns(Attempt());
            Store.SubmitReviewAsync(Arg.Do<BenchmarkReviewCommand>(command => Submitted = command), Arg.Any<CancellationToken>()).Returns(run);
            Service = new BenchmarkReviewService(Store);
        }

        public IBenchmarkStore Store { get; } = Substitute.For<IBenchmarkStore>();

        public IBenchmarkReviewService Service { get; }

        public BenchmarkReviewCommand? Submitted { get; private set; }

        private static BenchmarkJudgeAttemptRecord Attempt() =>
            new()
            {
                Id = AttemptId,
                RunId = RunId,
                Sequence = 1,
                PolicyRevisionId = RevisionId,
                CohortGeneration = 1,
                JudgeRuntimeJson = null,
                JudgeExecutionKey = "key",
                Status = BenchmarkJudgeAttemptStatus.Succeeded,
                ResultJson = BenchmarkJudgeSerialization.SerializeResult(new BenchmarkJudgeResultV2(BenchmarkJudgePolicyVersions.OutputSchemaVersion,
                    [
                        new BenchmarkJudgeCriterionScoreV2("accuracy", 6, "close"),
                        new BenchmarkJudgeCriterionScoreV2("clarity", 6, "fine"),
                        new BenchmarkJudgeCriterionScoreV2("exact_answer", BenchmarkJudgeVerifiers.PassScore, "matched")
                    ],
                    "summary",
                    Score: 68,
                    "v1:" + new string('c', count: 64),
                    [new BenchmarkJudgeVerifierResultV1("exact_answer", BenchmarkJudgeCriterionKinds.Exact, Passed: true, "matched")])),
                Score = 68,
                ErrorMessage = null,
                EnqueuedAtUtc = 1,
                StartedAtUtc = 1,
                CompletedAtUtc = 2,
                Version = 1
            };

        private static BenchmarkRunRecord Run(bool withPolicy, string judgeState, Guid judgeRevisionId) =>
            new()
            {
                Id = RunId,
                ProjectId = ProjectId,
                RuntimeSnapshotJson = new byte[]
                {
                    1
                },
                PrimaryModelName = "model.gguf",
                PrimaryModelOrigin = LocalModelOrigin.Imported,
                ModelContentFingerprint = $"v1:{new string('a', count: 64)}",
                AgentName = "Agent",
                AgentDefinitionId = Guid.Empty,
                AgentVersion = 1,
                RequestedContextTokens = 4096,
                PrimaryStatus = BenchmarkPrimaryStatus.Succeeded,
                EffectiveContextTokens = null,
                DurationMs = null,
                TotalTokens = null,
                TokensPerSecond = null,
                OutputPartsJson = null,
                LastStreamSequence = 0,
                UserScore = null,
                PrimaryErrorMessage = null,
                Version = 3,
                CreatedAtUtc = 1,
                StartedAtUtc = 1,
                PrimaryCompletedAtUtc = 1,
                UpdatedAtUtc = 1,
                Judge = withPolicy
                    ? new BenchmarkRunJudgeView
                    {
                        State = judgeState,
                        AttemptId = AttemptId,
                        Score = 68,
                        PolicyRevision = 1,
                        PolicyRevisionId = judgeRevisionId,
                        AttemptSequence = 1,
                        CohortGeneration = 1,
                        ExecutionKey = "key",
                        ErrorMessage = null,
                        PolicyCurrent = judgeRevisionId == RevisionId,
                        ExecutionCurrent = true,
                        RankExclusionReason = BenchmarkRunJudgeStates.ReasonReviewPending
                    }
                    : null
            };
    }
}
