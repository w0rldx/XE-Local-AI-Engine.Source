namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     The review gate on the ranking: in a review project a scoring run ranks on the reviewer's score alone, a pending, stale or rejected review is an
///     unranked state with a reason, and every path that returns a run agrees on it.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class BenchmarkReviewRankingStoreTests : IDisposable
{
    private const string HashA = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string HashB = "0000000000000000000000000000000000000000000000000000000000000002";
    private static readonly byte[] PolicyA = Encoding.UTF8.GetBytes("""{"rubric":"a"}""");
    private static readonly byte[] PolicyB = Encoding.UTF8.GetBytes("""{"rubric":"b"}""");
    private static readonly byte[] JudgeRuntime = Encoding.UTF8.GetBytes("""{"judgeRuntime":1}""");

    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task LoadRanking_WhenReviewProjectAndJudgeScored_ReportsReviewPending()
    {
        await using var context = await CreateContextAsync("review-rank-pending.sqlite");
        var store = NewStore(context);
        var project = await CreateJudgedProjectAsync(store, requiresReview: true);
        var run = await JudgedRunAsync(store, project, judgeScore: 80);

        var listed = await ListedAsync(store, project.Project.Id, run.Id);

        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonReviewPending, listed.Judge!.RankExclusionReason, "A current, succeeded judging does not rank a run awaiting review.");
        AssertEx.Null(listed.QualityScore);
        AssertEx.Equal(BenchmarkQualityScoreSources.None, listed.QualityScoreSource);
        AssertEx.Null(listed.Rank);
        AssertEx.Equal<int?>(80, listed.Judge.Score, "The judge score stays visible: it pre-fills the review form.");
    }

    [Test]
    public async Task LoadRanking_WhenApproved_RanksOnUserSourceOnly()
    {
        await using var context = await CreateContextAsync("review-rank-approved.sqlite");
        var store = NewStore(context);
        var project = await CreateJudgedProjectAsync(store, requiresReview: true);
        var run = await JudgedRunAsync(store, project, judgeScore: 80);

        _ = await store.SubmitReviewAsync(Approve(run, score: 60, project.Revision.Id));
        var listed = await ListedAsync(store, project.Project.Id, run.Id);

        AssertEx.Equal<int?>(60, listed.QualityScore, "The reviewer's score ranks, not the judge's 80.");
        AssertEx.Equal(BenchmarkQualityScoreSources.User, listed.QualityScoreSource);
        AssertEx.Null(listed.Judge!.RankExclusionReason);
        AssertEx.Equal<int?>(1, listed.Rank);
    }

    [Test]
    public async Task LoadRanking_WhenRejected_ExcludedEvenWithUserScore()
    {
        await using var context = await CreateContextAsync("review-rank-rejected.sqlite");
        var store = NewStore(context);
        var project = await CreateJudgedProjectAsync(store, requiresReview: true);
        var run = await JudgedRunAsync(store, project, judgeScore: 80);
        _ = await store.SubmitReviewAsync(Approve(run, score: 60, project.Revision.Id) with { State = BenchmarkReviewStates.Rejected });

        // A user score no write path leaves beside a rejection, planted directly: the rejection outranks it.
        _ = await context.BenchmarkRuns.Where(entity => entity.Id == run.Id).ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.UserScore, 95));
        var listed = await ListedAsync(store, project.Project.Id, run.Id);

        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonReviewRejected, listed.Judge!.RankExclusionReason);
        AssertEx.Null(listed.QualityScore, "A rejected run never ranks, whatever score sits on it.");
        AssertEx.Null(listed.Rank);
    }

    [Test]
    public async Task LoadRanking_WhenPolicyRevisionChanged_ReportsReviewStale()
    {
        await using var context = await CreateContextAsync("review-rank-stale.sqlite");
        var store = NewStore(context);
        var project = await CreateJudgedProjectAsync(store, requiresReview: true);
        var run = await JudgedRunAsync(store, project, judgeScore: 80);
        _ = await store.SubmitReviewAsync(Approve(run, score: 60, project.Revision.Id));

        var current = AssertEx.NotNull(await store.GetProjectAsync(project.Project.Id));
        _ = await store.ActivateJudgePolicyAsync(current.Id, current.Version, PolicyB, HashB, new BenchmarkJudgeAttemptSeed
        {
            RuntimeJson = new ReadOnlyMemory<byte>(JudgeRuntime)
        });
        var awaiting = await ListedAsync(store, project.Project.Id, run.Id);
        _ = await SucceedNextJudgingAsync(store, judgeScore: 75);
        var listed = await ListedAsync(store, project.Project.Id, run.Id);

        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonJudgePending, awaiting.Judge!.RankExclusionReason,
            "Until the re-judge under the new rubric succeeds, the review cannot reopen: the run waits on its judging.");
        AssertEx.Null(awaiting.QualityScore);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonReviewStale, listed.Judge!.RankExclusionReason, "A decision under a superseded rubric no longer ranks.");
        AssertEx.Null(listed.QualityScore);
        AssertEx.Equal<int?>(60, listed.UserScore, "The approved score is kept; it just does not rank until the run is reviewed again.");
    }

    [Test]
    public async Task LoadRanking_WhenReviewProjectAndJudgeFailed_KeepsTheJudgeReason()
    {
        await using var context = await CreateContextAsync("review-rank-judge-failed.sqlite");
        var store = NewStore(context);
        var project = await CreateJudgedProjectAsync(store, requiresReview: true);
        var run = await JudgedRunAsync(store, project, judgeScore: null);

        var listed = await ListedAsync(store, project.Project.Id, run.Id);
        var read = AssertEx.NotNull(await store.GetRunAsync(run.Id));
        var cells = await store.ListCellsAsync(project.Project.Id);

        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonJudgeFailed, listed.Judge!.RankExclusionReason,
            "A review cannot open on a failed judging, so the run reports what it needs: a re-judge.");
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonJudgeFailed, read.Judge!.RankExclusionReason);
        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonItemIncomplete, cells.Cells.Single().RankExclusionReason, "Not a review gap: the inbox does not list it.");
        AssertEx.Equal(expected: 0, (await store.ListReviewQueueAsync(new BenchmarkReviewQuery { State = BenchmarkReviewStates.Pending })).TotalCount);
    }

    [Test]
    public async Task LoadRanking_WhenPendingAndTruncated_ReportsTruncated()
    {
        await using var context = await CreateContextAsync("review-rank-truncated.sqlite");
        var store = NewStore(context);
        var project = await CreateJudgedProjectAsync(store, requiresReview: true);
        var run = await JudgedRunAsync(store, project, judgeScore: 80, stopReason: "length");

        var listed = await ListedAsync(store, project.Project.Id, run.Id);

        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonTruncated, listed.Judge!.RankExclusionReason,
            "Truncation is a fact about the answer a review cannot change, so it outranks the pending review.");
        AssertEx.Null(listed.QualityScore);
    }

    [Test]
    public async Task LoadRanking_WhenItemRevised_BeatsReviewStale()
    {
        await using var context = await CreateContextAsync("review-rank-item-revised.sqlite");
        var store = NewStore(context);
        var project = await CreateJudgedProjectAsync(store, requiresReview: true);
        var run = await JudgedRunAsync(store, project, judgeScore: 80);
        _ = await store.SubmitReviewAsync(Approve(run, score: 60, project.Revision.Id));

        var current = AssertEx.NotNull(await store.GetProjectAsync(project.Project.Id));
        _ = await store.ActivateJudgePolicyAsync(current.Id, current.Version, PolicyB, HashB);
        var item = (await store.ListTaskItemsAsync(project.Project.Id)).Single();
        _ = await store.UpdateTaskItemAsync(project.Project.Id, item.Id, item.Version, Prompt("q1, reworded"));
        var listed = await ListedAsync(store, project.Project.Id, run.Id);

        AssertEx.Equal(BenchmarkRunJudgeStates.ReasonItemRevised, listed.Judge!.RankExclusionReason,
            "An answer to a since-edited question is excluded before any review matters.");
    }

    [Test]
    public async Task LoadRanking_WhenReviewProject_NiahCaseKeepsRecallScore()
    {
        await using var context = await CreateContextAsync("review-rank-niah.sqlite");
        var store = NewStore(context);
        var project = await CreateJudgedProjectAsync(store, requiresReview: true);
        var probeId = Guid.NewGuid();
        var current = AssertEx.NotNull(await store.GetProjectAsync(project.Project.Id));
        _ = await store.CreateTaskItemAsync(current.Id, current.Version, Probe(probeId), [NiahCase(probeId)]);
        var caseIndex = (await store.ListTaskItemsAsync(current.Id)).Single(static item => item.Kind == BenchmarkTaskItemKinds.NiahCase).Index;
        var run = await JudgedRunAsync(store, project, judgeScore: 70, itemIndex: caseIndex);

        var listed = await ListedAsync(store, project.Project.Id, run.Id);
        var read = AssertEx.NotNull(await store.GetRunAsync(run.Id));
        var queue = await store.ListReviewQueueAsync(new BenchmarkReviewQuery { State = BenchmarkReviewStates.Pending });

        AssertEx.Equal<int?>(70, listed.QualityScore, "A display-only leaf keeps its judge score: it is the recall axis.");
        AssertEx.Equal(BenchmarkQualityScoreSources.Judge, listed.QualityScoreSource);
        AssertEx.Equal<int?>(70, read.QualityScore);
        AssertEx.Null(read.Judge!.RankExclusionReason, "The single-run read applies no review gate to it either.");
        AssertEx.False(queue.Items.Any(row => row.RunId == run.Id), "A NIAH case never enters the inbox.");
    }

    [Test]
    public async Task GetRun_AndWritePathRecord_AgreeWithLoadRankingOnTheGate()
    {
        await using var context = await CreateContextAsync("review-rank-paths.sqlite");
        var store = NewStore(context);
        var project = await CreateJudgedProjectAsync(store, requiresReview: true);
        var judged = await JudgedRunAsync(store, project, judgeScore: 80);

        // Pending: the judge success record is the write path the executor hands straight to the hub.
        AssertAgree(BenchmarkRunJudgeStates.ReasonReviewPending, null, judged, AssertEx.NotNull(await store.GetRunAsync(judged.Id)),
            await ListedAsync(store, project.Project.Id, judged.Id));

        var approved = await store.SubmitReviewAsync(Approve(judged, score: 60, project.Revision.Id));
        AssertAgree(null, 60, approved, AssertEx.NotNull(await store.GetRunAsync(judged.Id)), await ListedAsync(store, project.Project.Id, judged.Id));
    }

    [Test]
    public async Task Cancel_OfAReviewedRunWithArtifacts_ReturnsTheReviewAndTheArtifacts()
    {
        await using var context = await CreateContextAsync("review-cancel-detail.sqlite");
        var store = NewStore(context);
        var project = await CreateJudgedProjectAsync(store, requiresReview: true);
        var judged = await JudgedRunAsync(store, project, judgeScore: 80);
        _ = await store.AddRunArtifactsAsync(judged.Id, judged.Judge!.AttemptId!.Value, [
            new BenchmarkRunArtifactCommand
            {
                Id = Guid.NewGuid(),
                CriterionId = "tests",
                Kind = BenchmarkRunArtifactKinds.PythonTestsStdout,
                MediaType = "text/plain; charset=utf-8",
                ByteCount = 12,
                ContentHash = new string('f', count: 64)
            }
        ]);
        _ = await store.SubmitReviewAsync(Approve(judged, score: 60, project.Revision.Id));
        var current = AssertEx.NotNull(await store.GetProjectAsync(project.Project.Id));
        _ = await store.ActivateJudgePolicyAsync(current.Id, current.Version, PolicyB, HashB, new BenchmarkJudgeAttemptSeed
        {
            RuntimeJson = new ReadOnlyMemory<byte>(JudgeRuntime)
        });
        var requeued = AssertEx.NotNull(await store.GetRunAsync(judged.Id));

        var cancelled = await store.CancelAsync(judged.Id, requeued.Version);

        AssertEx.Equal(BenchmarkReviewStates.Approved, AssertEx.NotNull(cancelled.Review, "A write path answers the run detail, review included.").State);
        AssertEx.Equal(expected: 1, cancelled.Artifacts.Count, "A write path answers the run detail, artifacts included.");
    }

    [Test]
    public async Task Page_CarriesTheStoredReviewState()
    {
        await using var context = await CreateContextAsync("review-state-page.sqlite");
        var store = NewStore(context);
        var project = await CreateJudgedProjectAsync(store, requiresReview: true);
        var reviewed = await JudgedRunAsync(store, project, judgeScore: 80);
        var unreviewed = await JudgedRunAsync(store, project, judgeScore: 70);

        var approved = await store.SubmitReviewAsync(Approve(reviewed, score: 60, project.Revision.Id));

        AssertEx.Equal(BenchmarkReviewStates.Approved, (await ListedAsync(store, project.Project.Id, reviewed.Id)).ReviewState,
            "The listing reads the stored row's state, not the exclusion reason.");
        AssertEx.Null((await ListedAsync(store, project.Project.Id, unreviewed.Id)).ReviewState, "No row, no state.");
        AssertEx.Equal(BenchmarkReviewStates.Approved, AssertEx.NotNull(await store.GetRunAsync(reviewed.Id)).ReviewState);
        AssertEx.Equal(BenchmarkReviewStates.Approved, approved.ReviewState, "The write path agrees with the reads.");
    }

    [Test]
    public async Task Page_CarriesTheReviewPolicyCurrency()
    {
        await using var context = await CreateContextAsync("review-policy-current.sqlite");
        var store = NewStore(context);
        var project = await CreateJudgedProjectAsync(store, requiresReview: true);
        var reviewed = await JudgedRunAsync(store, project, judgeScore: 80);
        var unreviewed = await JudgedRunAsync(store, project, judgeScore: 70);
        var approved = await store.SubmitReviewAsync(Approve(reviewed, score: 60, project.Revision.Id));

        var current = await ListedAsync(store, project.Project.Id, reviewed.Id);
        var latest = AssertEx.NotNull(await store.GetProjectAsync(project.Project.Id));
        _ = await store.ActivateJudgePolicyAsync(latest.Id, latest.Version, PolicyB, HashB);
        var superseded = await ListedAsync(store, project.Project.Id, reviewed.Id);

        AssertEx.Equal<bool?>(true, approved.ReviewPolicyCurrent, "Approved under the current revision.");
        AssertEx.Equal<bool?>(true, current.ReviewPolicyCurrent);
        AssertEx.Equal<bool?>(false, superseded.ReviewPolicyCurrent, "A new revision supersedes the decision.");
        AssertEx.Equal<bool?>(false, AssertEx.NotNull(await store.GetRunAsync(reviewed.Id)).ReviewPolicyCurrent);
        AssertEx.Null((await ListedAsync(store, project.Project.Id, unreviewed.Id)).ReviewPolicyCurrent, "No row, no currency.");
    }

    [Test]
    public async Task LoadRanking_WhenProjectDoesNotRequireReview_IsUnchanged()
    {
        await using var context = await CreateContextAsync("review-rank-unchanged.sqlite");
        var store = NewStore(context);
        var project = await CreateJudgedProjectAsync(store, requiresReview: false);
        var judged = await JudgedRunAsync(store, project, judgeScore: 80);
        var overridden = await JudgedRunAsync(store, project, judgeScore: 40);
        _ = await store.SetUserScoreAsync(overridden.Id, 90, overridden.Version);

        var page = await store.ListRunsAsync(project.Project.Id, skip: 0, take: 10);
        var byId = page.Items.ToDictionary(static run => run.Id);

        AssertEx.Equal<int?>(80, byId[judged.Id].QualityScore, "Without the flag a current judge score ranks exactly as before.");
        AssertEx.Equal(BenchmarkQualityScoreSources.Judge, byId[judged.Id].QualityScoreSource);
        AssertEx.Equal<int?>(2, byId[judged.Id].Rank);
        AssertEx.Equal(BenchmarkQualityScoreSources.User, byId[overridden.Id].QualityScoreSource, "The operator override still outranks the judge.");
        AssertEx.Equal<int?>(1, byId[overridden.Id].Rank);
        AssertEx.Equal(expected: 2, AssertEx.NotNull(page.RankCohort).TotalScored);
    }

    private static void AssertAgree(string? reason, int? quality, params BenchmarkRunRecord[] records)
    {
        foreach (var record in records)
        {
            AssertEx.Equal(reason, record.Judge!.RankExclusionReason, "Every path that returns a run reports the same gate.");
            AssertEx.Equal(quality, record.QualityScore);
            AssertEx.Equal(quality is null ? BenchmarkQualityScoreSources.None : BenchmarkQualityScoreSources.User, record.QualityScoreSource);
        }
    }

    private static async Task<BenchmarkRunRecord> ListedAsync(BenchmarkStore store, Guid projectId, Guid runId) =>
        (await store.ListRunsAsync(projectId, skip: 0, take: 50)).Items.Single(run => run.Id == runId);

    private static BenchmarkReviewCommand Approve(BenchmarkRunRecord run, int score, Guid revisionId) =>
        new()
        {
            RunId = run.Id,
            ExpectedRunVersion = run.Version,
            State = BenchmarkReviewStates.Approved,
            Score = score,
            PolicyRevisionId = revisionId
        };

    private static async Task<JudgedProject> CreateJudgedProjectAsync(BenchmarkStore store, bool requiresReview)
    {
        var project = await store.CreateProjectAsync(new BenchmarkProjectInput
        {
            Id = Guid.NewGuid(),
            Name = "Benchmark",
            CoreTaskJson = Encoding.UTF8.GetBytes("""{"task":"answer"}"""),
            ContextTokens = 4096,
            AgentDefinitionId = Guid.NewGuid(),
            RequiresReview = requiresReview
        }, initialItems: [Prompt("q1")]);
        var activation = await store.ActivateJudgePolicyAsync(project.Id, project.Version, PolicyA, HashA);
        return new JudgedProject
        {
            Project = project,
            Revision = activation.Revision
        };
    }

    /// <summary>A run on the item at <paramref name="itemIndex" />, stamped as freeze stamps it, judged to <paramref name="judgeScore" /> in the live cohort.</summary>
    /// <remarks>A <see langword="null" /> <paramref name="judgeScore" /> fails the judging instead.</remarks>
    private static async Task<BenchmarkRunRecord> JudgedRunAsync(BenchmarkStore store, JudgedProject judged, int? judgeScore, string stopReason = "stop", int itemIndex = 0)
    {
        var project = AssertEx.NotNull(await store.GetProjectAsync(judged.Project.Id));
        var item = (await store.ListTaskItemsAsync(project.Id)).Single(entry => entry.Index == itemIndex);
        var run = await store.StartRunAsync(new BenchmarkStartRunCommand
        {
            RunId = Guid.NewGuid(),
            ProjectId = project.Id,
            ExpectedProjectVersion = project.Version,
            RuntimeSnapshotJson = Encoding.UTF8.GetBytes("""{"schemaVersion":1}"""),
            PrimaryModelName = "model.gguf",
            PrimaryModelOrigin = LocalModelOrigin.Imported,
            ModelContentFingerprint = "v1:" + new string('a', count: 64),
            AgentName = "Agent",
            AgentVersion = 1,
            RequestedContextTokens = 4096,
            TaskItemId = item.Id,
            TaskItemIndex = item.Index,
            CellKey = "cell:" + Guid.NewGuid().ToString("D"),
            TaskInputHash = item.InputHash,
            TaskItemSetHash = project.TaskItemSetHash
        });
        var primary = AssertEx.NotNull(await store.ClaimNextAsync());
        _ = await store.MarkPrimarySucceededAsync(new BenchmarkPrimarySuccessCommand
        {
            RunId = run.Id,
            ExpectedWorkVersion = primary.Run.Version,
            OutputPartsJson = Encoding.UTF8.GetBytes("""[{"text":"answer"}]"""),
            LastStreamSequence = 1,
            EffectiveContextTokens = 4096,
            DurationMs = 10,
            TotalTokens = 12,
            TokensPerSecond = 120,
            PrimaryStopReason = stopReason,
            JudgeAttempt = new BenchmarkJudgeAttemptSeed
            {
                ExpectedJudgePolicyRevisionId = judged.Revision.Id,
                RuntimeJson = new ReadOnlyMemory<byte>(JudgeRuntime)
            }
        });
        if (judgeScore is { } score)
        {
            return await SucceedNextJudgingAsync(store, score);
        }

        var judge = AssertEx.NotNull(await store.ClaimNextAsync());
        return await store.MarkJudgeFailedAsync(judge.RunId, judge.Version, "judge blew up");
    }

    private static async Task<BenchmarkRunRecord> SucceedNextJudgingAsync(BenchmarkStore store, int judgeScore)
    {
        var judge = AssertEx.NotNull(await store.ClaimNextAsync());
        _ = await store.MarkJudgeLaunchReadyAsync(judge.JudgeAttemptId!.Value, judge.QueueSequence, judge.Version, Receipt(), "key-a");
        return await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand
        {
            RunId = judge.RunId,
            ExpectedWorkVersion = judge.Version,
            JudgeResultJson = Encoding.UTF8.GetBytes("{}"),
            LastStreamSequence = 5,
            Score = judgeScore
        });
    }

    private static BenchmarkLaunchReceiptCommand Receipt() =>
        new()
        {
            ReceiptJson = "{}",
            EnvironmentFactsJson = "{}",
            EnvironmentFactsHash = new string('e', count: 64),
            ReceiptHash = new string('r', count: 64),
            EffectiveLaunchIdentity = "identity",
            EffectiveBackend = "cpu",
            PlacementOffloaded = null,
            PlacementTotal = null,
            ExecutableSha256 = new string('x', count: 64),
            HasAuxAssets = false,
            KvCacheTypeSource = "auto"
        };

    private static BenchmarkTaskItemInput Prompt(string prompt) =>
        new()
        {
            PromptJson = Encoding.UTF8.GetBytes(prompt)
        };

    private static BenchmarkTaskItemInput Probe(Guid id) =>
        new()
        {
            PromptJson = Encoding.UTF8.GetBytes("a long-context probe"),
            Kind = BenchmarkTaskItemKinds.Niah,
            GeneratorConfigJson = Encoding.UTF8.GetBytes("""{"contextTokens":[2048]}"""),
            Id = id
        };

    private static BenchmarkTaskItemInput NiahCase(Guid parentId) =>
        new()
        {
            PromptJson = Encoding.UTF8.GetBytes("case-a"),
            Kind = BenchmarkTaskItemKinds.NiahCase,
            VerifierConfigJson = Encoding.UTF8.GetBytes("""{"recall":{"expected":"ABC123"}}"""),
            GeneratorConfigJson = Encoding.UTF8.GetBytes("""{"contextTokens":2048,"depthPercent":50}"""),
            ParentItemId = parentId,
            CountsTowardScore = false
        };

    private static BenchmarkStore NewStore(NodeChatDbContext context) =>
        new(context, TimeProvider.System, NullLogger<BenchmarkStore>.Instance);

    private async Task<NodeChatDbContext> CreateContextAsync(string fileName)
    {
        _ = Directory.CreateDirectory(_rootPath);
        var context = AgentDefinitionTestContextFactory.Create(Path.Combine(_rootPath, fileName), _keyHolder);
        _ = await context.Database.EnsureDeletedAsync();
        _ = await context.Database.EnsureCreatedAsync();
        return context;
    }

    private sealed class JudgedProject
    {
        public required BenchmarkProjectRecord Project { get; init; }

        public required BenchmarkJudgePolicyRevisionRecord Revision { get; init; }
    }
}
