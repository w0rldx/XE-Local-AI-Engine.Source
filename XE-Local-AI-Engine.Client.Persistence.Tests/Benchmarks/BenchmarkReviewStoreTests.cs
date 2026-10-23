namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     The store half of the review phase: decisions only on reviewable scoring runs, an approval IS the user score, a
///     rejection clears it, pending is derived from flat columns, and a review never blocks a delete.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class BenchmarkReviewStoreTests : IDisposable
{
    private const string PolicyHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
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
    public async Task SubmitReview_WhenApproved_WritesUserScoreAndBumpsRunVersion()
    {
        await using var context = await CreateContextAsync("review-approved.sqlite");
        var store = NewStore(context);
        var project = await CreateReviewProjectAsync(store, Prompt("q1"));
        var run = await StartSucceededRunAsync(store, project.Id, itemIndex: 0);

        var reviewed = await store.SubmitReviewAsync(Approve(run, score: 72) with
        {
            Criteria =
            [
                new BenchmarkReviewCriterionScore
                {
                    CriterionId = "accuracy",
                    Score = 7
                }
            ],
            Notes = "Correct, but terse."
        });

        AssertEx.Equal<int?>(72, reviewed.UserScore, "An approval writes its overall score onto the run's user score.");
        AssertEx.Equal(run.Version + 1, reviewed.Version, "The decision is a run transition and bumps the run version once.");
        var review = AssertEx.NotNull(reviewed.Review, "The write-path record carries the decision it just made.");
        AssertEx.Equal(BenchmarkReviewStates.Approved, review.State);
        AssertEx.Equal("accuracy", review.Criteria.Single().CriterionId);
        AssertEx.Equal(expected: 7, review.Criteria.Single().Score);

        var readBack = AssertEx.NotNull(await store.GetRunAsync(run.Id));
        AssertEx.Equal<int?>(72, readBack.UserScore);
        AssertEx.Equal("Correct, but terse.", AssertEx.NotNull(readBack.Review).Notes, "The single-run read carries the decision, notes included.");
    }

    [Test]
    public async Task SubmitReview_WhenRejected_ClearsUserScore()
    {
        await using var context = await CreateContextAsync("review-rejected.sqlite");
        var store = NewStore(context);
        var project = await CreateReviewProjectAsync(store, Prompt("q1"));
        var run = await StartSucceededRunAsync(store, project.Id, itemIndex: 0);
        var approved = await store.SubmitReviewAsync(Approve(run, score: 80));

        var rejected = await store.SubmitReviewAsync(new BenchmarkReviewCommand
        {
            RunId = run.Id,
            ExpectedRunVersion = approved.Version,
            State = BenchmarkReviewStates.Rejected,
            Score = 55
        });

        AssertEx.Null(rejected.UserScore, "A rejection clears the user score an earlier approval wrote.");
        var review = AssertEx.NotNull(rejected.Review);
        AssertEx.Equal(BenchmarkReviewStates.Rejected, review.State, "The latest decision wins: the row is overwritten, not duplicated.");
        AssertEx.Null(review.Score, "A rejection stores no score, whatever the caller sent.");
        AssertEx.Equal(expected: 2L, review.Version);
        AssertEx.Equal(expected: 1, await context.BenchmarkRunReviews.CountAsync(), "One decision per run.");
    }

    [Test]
    public async Task SubmitReview_WhenApprovedWithoutScore_IsRefusedByTheStore()
    {
        await using var context = await CreateContextAsync("review-approved-no-score.sqlite");
        var store = NewStore(context);
        var project = await CreateReviewProjectAsync(store, Prompt("q1"));
        var run = await StartSucceededRunAsync(store, project.Id, itemIndex: 0);

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => store.SubmitReviewAsync(Approve(run, score: 0) with
            {
                Score = null
            }),
            "An approval with no score would rank nothing; the schema's CHECK is the backstop behind this refusal.");

        AssertEx.Equal(expected: 0, await context.BenchmarkRunReviews.CountAsync());
        AssertEx.Equal(run.Version, AssertEx.NotNull(await store.GetRunAsync(run.Id)).Version, "A refused decision leaves the run untouched.");
    }

    [Test]
    public async Task SubmitReview_WhenRunNotSucceeded_Refuses()
    {
        await using var context = await CreateContextAsync("review-not-succeeded.sqlite");
        var store = NewStore(context);
        var project = await CreateReviewProjectAsync(store, Prompt("q1"));
        var queued = await StartRunAsync(store, project.Id, itemIndex: 0);

        var conflict = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.SubmitReviewAsync(Approve(queued, score: 50)));

        AssertEx.Equal("NotReviewable", conflict.Code);
        AssertEx.Equal(expected: 0, await context.BenchmarkRunReviews.CountAsync(), "A refused decision writes nothing.");
    }

    [Test]
    public async Task SubmitReview_WhenWarmup_Refuses()
    {
        await using var context = await CreateContextAsync("review-warmup.sqlite");
        var store = NewStore(context);
        var project = await CreateReviewProjectAsync(store, Prompt("q1"));
        var warmup = await StartSucceededRunAsync(store, project.Id, itemIndex: 0, warmup: true);

        var conflict = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.SubmitReviewAsync(Approve(warmup, score: 50)));

        AssertEx.Equal("NotReviewable", conflict.Code, "A warm-up never ranks, so there is nothing to review.");
    }

    [Test]
    public async Task SubmitReview_WhenNonScoringLeaf_Refuses()
    {
        await using var context = await CreateContextAsync("review-non-scoring.sqlite");
        var store = NewStore(context);
        var project = await CreateReviewProjectAsync(store, Prompt("q1"), Prompt("display only") with
        {
            CountsTowardScore = false
        });
        var displayOnly = await StartSucceededRunAsync(store, project.Id, itemIndex: 1);

        var conflict = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.SubmitReviewAsync(Approve(displayOnly, score: 50)));

        AssertEx.Equal("NotReviewable", conflict.Code, "A non-scoring leaf keeps its judge path; a review would have nothing to rank.");
    }

    [Test]
    public async Task SubmitReview_WhenProjectDoesNotRequireReview_Refuses()
    {
        await using var context = await CreateContextAsync("review-not-required.sqlite");
        var store = NewStore(context);
        var project = await store.CreateProjectAsync(NewProject(requiresReview: false), initialItems: [Prompt("q1")]);
        var run = await StartSucceededRunAsync(store, project.Id, itemIndex: 0);

        var conflict = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.SubmitReviewAsync(Approve(run, score: 50)));

        AssertEx.Equal("NotReviewable", conflict.Code);
        AssertEx.Null(AssertEx.NotNull(await store.GetRunAsync(run.Id)).UserScore, "A refused decision leaves the user score alone.");
    }

    [Test]
    public async Task SubmitReview_WhenItemRevised_ThrowsNotReviewable()
    {
        await using var context = await CreateContextAsync("review-item-revised.sqlite");
        var store = NewStore(context);
        var project = await CreateReviewProjectAsync(store, Prompt("q1"));
        var run = await StartSucceededRunAsync(store, project.Id, itemIndex: 0);
        var item = (await store.ListTaskItemsAsync(project.Id)).Single();
        _ = await store.UpdateTaskItemAsync(project.Id, item.Id, item.Version, Prompt("q1, reworded"));
        var current = AssertEx.NotNull(await store.GetRunAsync(run.Id));

        var refusal = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.SubmitReviewAsync(Approve(current, score: 70)));

        AssertEx.Equal("NotReviewable", refusal.Code, "A run that can never rank is not worth a reviewer's time.");
        AssertEx.Equal(expected: 0, await context.BenchmarkRunReviews.CountAsync(), "The refusal writes no decision.");
        AssertEx.Null(AssertEx.NotNull(await store.GetRunAsync(run.Id)).UserScore);
    }

    [Test]
    public async Task SubmitReview_WhenTwoReviewersUseTheSameRunVersion_SecondGetsVersionConflict()
    {
        await using var context = await CreateContextAsync("review-cas.sqlite");
        var store = NewStore(context);
        var project = await CreateReviewProjectAsync(store, Prompt("q1"));
        var run = await StartSucceededRunAsync(store, project.Id, itemIndex: 0);

        var first = await store.SubmitReviewAsync(Approve(run, score: 70));
        var second = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.SubmitReviewAsync(Approve(run, score: 20) with
        {
            State = BenchmarkReviewStates.Rejected
        }));

        AssertEx.Equal("VersionConflict", second.Code, "Both read the same run version; only the first decision may land.");
        var stored = AssertEx.NotNull(await store.GetRunAsync(run.Id));
        AssertEx.Equal<int?>(70, stored.UserScore, "The first decision stands.");
        AssertEx.Equal(BenchmarkReviewStates.Approved, AssertEx.NotNull(stored.Review).State);
        AssertEx.Equal(first.Version, stored.Version, "The refused write bumped nothing.");
    }

    [Test]
    public async Task SubmitReview_WhenStaleVersion_ThrowsVersionConflict()
    {
        await using var context = await CreateContextAsync("review-stale-version.sqlite");
        var store = NewStore(context);
        var project = await CreateReviewProjectAsync(store, Prompt("q1"));
        var run = await StartSucceededRunAsync(store, project.Id, itemIndex: 0);
        _ = await store.SubmitReviewAsync(Approve(run, score: 60));

        var conflict = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.SubmitReviewAsync(Approve(run, score: 90)));

        AssertEx.Equal("VersionConflict", conflict.Code, "A second reviewer holding the old version must not overwrite the first decision.");
        AssertEx.Equal<int?>(60, AssertEx.NotNull(await store.GetRunAsync(run.Id)).UserScore);
    }

    [Test]
    public async Task SetUserScore_WhenProjectRequiresReview_ThrowsReviewRequired()
    {
        await using var context = await CreateContextAsync("review-set-user-score.sqlite");
        var store = NewStore(context);
        var project = await CreateReviewProjectAsync(store, Prompt("q1"));
        var run = await StartSucceededRunAsync(store, project.Id, itemIndex: 0);

        var set = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.SetUserScoreAsync(run.Id, 40, run.Version));
        var clear = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.SetUserScoreAsync(run.Id, score: null, run.Version));

        AssertEx.Equal("ReviewRequired", set.Code, "In a review project the review is the only writer of the user score.");
        AssertEx.Equal("ReviewRequired", clear.Code, "Clearing is a write too: it would silently undo an approval.");
        AssertEx.Null(AssertEx.NotNull(await store.GetRunAsync(run.Id)).UserScore);
    }

    [Test]
    public async Task ListReviewQueue_Pending_ExcludesWarmupsNonScoringLeavesAndStaleItems()
    {
        await using var context = await CreateContextAsync("review-queue-pending.sqlite");
        var store = NewStore(context);
        var project = await CreateReviewProjectAsync(store, Prompt("q1"), Prompt("q2"), Prompt("display only") with
        {
            CountsTowardScore = false
        });
        var pending = await StartSucceededRunAsync(store, project.Id, itemIndex: 0);
        var second = await StartSucceededRunAsync(store, project.Id, itemIndex: 1);
        _ = await StartSucceededRunAsync(store, project.Id, itemIndex: 0, warmup: true);
        _ = await StartSucceededRunAsync(store, project.Id, itemIndex: 2);
        var approved = await StartSucceededRunAsync(store, project.Id, itemIndex: 0);
        _ = await store.SubmitReviewAsync(Approve(approved, score: 70));

        var page = await store.ListReviewQueueAsync(new BenchmarkReviewQuery
        {
            State = BenchmarkReviewStates.Pending,
            ProjectId = project.Id
        });

        AssertEx.Equal(expected: 2, page.TotalCount, "Only the succeeded, scoring, undecided runs are pending: no warm-up, no display-only leaf, no decided run.");
        AssertEx.True(page.Items.Select(static row => row.RunId).ToHashSet().SetEquals([pending.Id, second.Id]));
        AssertEx.True(page.Items.All(static row => row.State == BenchmarkReviewStates.Pending && row.ReviewScore is null));
        var approvedPage = await store.ListReviewQueueAsync(new BenchmarkReviewQuery
        {
            State = BenchmarkReviewStates.Approved,
            ProjectId = project.Id
        });
        AssertEx.Equal(approved.Id, approvedPage.Items.Single().RunId);
        AssertEx.Equal<int?>(70, approvedPage.Items.Single().ReviewScore);
        AssertEx.Equal(expected: 0, (await store.ListReviewQueueAsync(new BenchmarkReviewQuery
        {
            State = BenchmarkReviewStates.Rejected
        })).TotalCount);

        // Editing an item moves its input hash AND the project's set hash: every run was measured against a question set that no longer exists, and the
        // ranking excludes them before any review matters, so none of them is reviewable.
        var item = (await store.ListTaskItemsAsync(project.Id)).Single(entry => entry.Index == 1);
        _ = await store.UpdateTaskItemAsync(project.Id, item.Id, item.Version, Prompt("q2, reworded"));
        _ = await StartRunAsync(store, project.Id, itemIndex: 0);

        AssertEx.Equal(expected: 0, (await store.ListReviewQueueAsync(new BenchmarkReviewQuery
            {
                State = BenchmarkReviewStates.Pending
            })).TotalCount,
            "Runs on a revised item set and a queued run are not pending.");
        AssertEx.Equal(expected: 0, (await store.ListReviewQueueAsync(new BenchmarkReviewQuery
        {
            State = BenchmarkReviewStates.Approved
        })).TotalCount);
    }

    [Test]
    public async Task ListReviewQueue_Pending_IncludesReviewsUnderASupersededRevision()
    {
        await using var context = await CreateContextAsync("review-queue-stale.sqlite");
        var store = NewStore(context);
        var project = await CreateReviewProjectAsync(store, Prompt("q1"));
        var run = await StartSucceededRunAsync(store, project.Id, itemIndex: 0);
        _ = await store.SubmitReviewAsync(Approve(run, score: 70));
        AssertEx.Equal(expected: 0, (await store.ListReviewQueueAsync(new BenchmarkReviewQuery
            {
                State = BenchmarkReviewStates.Pending
            })).TotalCount,
            "A decision under the current revision (no policy) is not pending.");

        // The rubric changes: the project now judges under a revision the decision was not made under, and the re-judge succeeds.
        var current = AssertEx.NotNull(await store.GetProjectAsync(project.Id));
        _ = await store.ActivateJudgePolicyAsync(project.Id, current.Version, Encoding.UTF8.GetBytes("""{"rubric":"v2"}"""), PolicyHash, JudgeSeed(revisionId: null));
        AssertEx.Equal(expected: 0, (await store.ListReviewQueueAsync(new BenchmarkReviewQuery
            {
                State = BenchmarkReviewStates.Pending
            })).TotalCount,
            "Not back in the inbox while its re-judge is still queued.");
        _ = await SucceedNextJudgingAsync(store);

        var pending = await store.ListReviewQueueAsync(new BenchmarkReviewQuery
        {
            State = BenchmarkReviewStates.Pending
        });
        var stale = await store.ListReviewQueueAsync(new BenchmarkReviewQuery
        {
            State = BenchmarkReviewStates.Stale
        });
        var approved = await store.ListReviewQueueAsync(new BenchmarkReviewQuery
        {
            State = BenchmarkReviewStates.Approved
        });

        AssertEx.Equal(run.Id, pending.Items.Single().RunId, "A stale review returns to the inbox.");
        AssertEx.Equal(BenchmarkReviewStates.Stale, pending.Items.Single().State, "The row says why it is back.");
        AssertEx.Equal(run.Id, stale.Items.Single().RunId);
        AssertEx.Equal(expected: 0, approved.TotalCount, "A decision under a superseded revision no longer counts as approved.");
    }

    [Test]
    public async Task ListReviewQueue_Pending_ExcludesRunsWhoseJudgingIsNotSucceededUnderTheCurrentRevision()
    {
        await using var context = await CreateContextAsync("review-queue-judging.sqlite");
        var store = NewStore(context);
        var created = await CreateReviewProjectAsync(store, Prompt("q1"));
        var revision = (await store.ActivateJudgePolicyAsync(created.Id, created.Version, Encoding.UTF8.GetBytes("""{"rubric":"v1"}"""), PolicyHash)).Revision;
        // Frozen first, so the FIFO queue drains the three primaries before any judging, then the judgings in freeze order.
        var judged = await StartRunAsync(store, created.Id, itemIndex: 0);
        var failed = await StartRunAsync(store, created.Id, itemIndex: 0);
        var queued = await StartRunAsync(store, created.Id, itemIndex: 0);
        foreach (var run in new[]
                 {
                     judged,
                     failed,
                     queued
                 })
        {
            _ = await SucceedClaimedPrimaryAsync(store, run, revision.Id);
        }

        AssertEx.Equal(judged.Id, (await SucceedNextJudgingAsync(store)).Id);
        var failing = AssertEx.NotNull(await store.ClaimNextAsync());
        AssertEx.Equal(failed.Id, failing.RunId);
        _ = await store.MarkJudgeFailedAsync(failing.RunId, failing.Version, "judge blew up");

        var page = await store.ListReviewQueueAsync(new BenchmarkReviewQuery
        {
            State = BenchmarkReviewStates.Pending
        });

        AssertEx.Equal(judged.Id, page.Items.Single().RunId, "Only the run the review service would accept is listed: the queued and the failed judging are not.");
        AssertEx.False(page.Items.Any(row => row.RunId == queued.Id || row.RunId == failed.Id));
    }

    [Test]
    public async Task SubmitReview_WhenNotesExceedTheCap_Rejects()
    {
        await using var context = await CreateContextAsync("review-notes-cap.sqlite");
        var store = NewStore(context);
        var project = await CreateReviewProjectAsync(store, Prompt("q1"));
        var run = await StartSucceededRunAsync(store, project.Id, itemIndex: 0);

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            store.SubmitReviewAsync(Approve(run, score: 70) with
            {
                Notes = new string('n', BenchmarkReviewLimits.MaxNotesCharacters + 1)
            }));
        var atCap = await store.SubmitReviewAsync(Approve(run, score: 70) with
        {
            Notes = new string('n', BenchmarkReviewLimits.MaxNotesCharacters)
        });

        AssertEx.Equal(BenchmarkReviewLimits.MaxNotesCharacters, AssertEx.NotNull(AssertEx.NotNull(atCap.Review).Notes).Length, "The cap itself is accepted.");
    }

    [Test]
    public async Task ListReviewQueue_PagesAndRejectsAnUnknownState()
    {
        await using var context = await CreateContextAsync("review-queue-paging.sqlite");
        var store = NewStore(context);
        var project = await CreateReviewProjectAsync(store, Prompt("q1"));
        for (var index = 0; index < 3; index++)
        {
            _ = await StartSucceededRunAsync(store, project.Id, itemIndex: 0);
        }

        var first = await store.ListReviewQueueAsync(new BenchmarkReviewQuery
        {
            State = BenchmarkReviewStates.Pending,
            Take = 2
        });
        var second = await store.ListReviewQueueAsync(new BenchmarkReviewQuery
        {
            State = BenchmarkReviewStates.Pending,
            Skip = 2,
            Take = 2
        });

        AssertEx.Equal(expected: 2, first.Items.Count);
        AssertEx.Equal(expected: 1, second.Items.Count);
        AssertEx.Equal(expected: 3, second.TotalCount, "The total counts the whole filter, not the page.");
        AssertEx.False(first.Items.Any(row => row.RunId == second.Items[0].RunId), "Pages do not overlap.");
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => store.ListReviewQueueAsync(new BenchmarkReviewQuery
        {
            State = "maybe"
        }));
    }

    [Test]
    public async Task DeleteRun_WhenReviewPending_IsNotRefused()
    {
        await using var context = await CreateContextAsync("review-delete-pending.sqlite");
        var store = NewStore(context);
        var project = await CreateReviewProjectAsync(store, Prompt("q1"));
        var run = await StartSucceededRunAsync(store, project.Id, itemIndex: 0);
        AssertEx.Equal(expected: 1, (await store.ListReviewQueueAsync(new BenchmarkReviewQuery
        {
            State = BenchmarkReviewStates.Pending
        })).TotalCount);

        await store.DeleteRunAsync(run.Id, run.Version);

        AssertEx.Null(await store.GetRunAsync(run.Id), "A pending review is not activity: the run is deletable.");
    }

    [Test]
    public async Task DeleteRun_WhenReviewed_DeletesTheReviewRow()
    {
        await using var context = await CreateContextAsync("review-delete-reviewed.sqlite");
        var store = NewStore(context);
        var project = await CreateReviewProjectAsync(store, Prompt("q1"));
        var run = await StartSucceededRunAsync(store, project.Id, itemIndex: 0);
        var reviewed = await store.SubmitReviewAsync(Approve(run, score: 70) with
        {
            Notes = "keep"
        });

        await store.DeleteRunAsync(run.Id, reviewed.Version);

        AssertEx.Equal(expected: 0, await context.BenchmarkRunReviews.CountAsync(), "The review goes with its run.");
    }

    private static BenchmarkReviewCommand Approve(BenchmarkRunRecord run, int score) =>
        new()
        {
            RunId = run.Id,
            ExpectedRunVersion = run.Version,
            State = BenchmarkReviewStates.Approved,
            Score = score
        };

    private static BenchmarkTaskItemInput Prompt(string prompt) =>
        new()
        {
            PromptJson = Encoding.UTF8.GetBytes(prompt)
        };

    private static BenchmarkProjectInput NewProject(bool requiresReview) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = "Benchmark",
            CoreTaskJson = Encoding.UTF8.GetBytes("""{"task":"answer"}"""),
            ContextTokens = 4096,
            AgentDefinitionId = Guid.NewGuid(),
            RequiresReview = requiresReview
        };

    private static async Task<BenchmarkProjectRecord> CreateReviewProjectAsync(BenchmarkStore store, params BenchmarkTaskItemInput[] items)
    {
        var project = await store.CreateProjectAsync(NewProject(requiresReview: true), initialItems: items);
        AssertEx.True(project.RequiresReview, "The flag round-trips through the create path.");
        return project;
    }

    /// <summary>Freezes one run on the item at <paramref name="itemIndex" />, stamped exactly as freeze stamps it.</summary>
    private static async Task<BenchmarkRunRecord> StartRunAsync(BenchmarkStore store, Guid projectId, int itemIndex, bool warmup = false)
    {
        var project = AssertEx.NotNull(await store.GetProjectAsync(projectId));
        var item = (await store.ListTaskItemsAsync(projectId)).Single(entry => entry.Index == itemIndex);
        return await store.StartRunAsync(new BenchmarkStartRunCommand
        {
            RunId = Guid.NewGuid(),
            ProjectId = project.Id,
            ExpectedProjectVersion = project.Version,
            RuntimeSnapshotJson = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}"),
            PrimaryModelName = "model.gguf",
            PrimaryModelOrigin = LocalModelOrigin.Imported,
            ModelContentFingerprint = "v1:" + new string('a', 64),
            AgentName = "Agent",
            AgentVersion = 1,
            RequestedContextTokens = 4096,
            TaskItemId = item.Id,
            TaskItemIndex = item.Index,
            CellKey = "cell:" + Guid.NewGuid().ToString("D"),
            TaskInputHash = item.InputHash,
            TaskItemSetHash = project.TaskItemSetHash,
            IsWarmup = warmup,
            RepeatGroupId = warmup ? Guid.NewGuid() : null,
            RepeatIndex = warmup ? 0 : null
        });
    }

    private static BenchmarkJudgeAttemptSeed JudgeSeed(Guid? revisionId) =>
        new()
        {
            ExpectedJudgePolicyRevisionId = revisionId,
            RuntimeJson = Encoding.UTF8.GetBytes("""{"judgeRuntime":1}""")
        };

    private static async Task<BenchmarkRunRecord> SucceedNextJudgingAsync(BenchmarkStore store)
    {
        var judge = AssertEx.NotNull(await store.ClaimNextAsync());
        return await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand
        {
            RunId = judge.RunId,
            ExpectedWorkVersion = judge.Version,
            JudgeResultJson = Encoding.UTF8.GetBytes("{}"),
            LastStreamSequence = 5,
            Score = 80,
            VerifiedExecutionKey = "verified"
        });
    }

    private static async Task<BenchmarkRunRecord> StartSucceededRunAsync(BenchmarkStore store, Guid projectId, int itemIndex, bool warmup = false) =>
        await SucceedClaimedPrimaryAsync(store, await StartRunAsync(store, projectId, itemIndex, warmup), judgeRevisionId: null);

    /// <summary>Claims the next work item, which must be <paramref name="run" />'s primary, and succeeds it, seeding a judging when a revision is named.</summary>
    private static async Task<BenchmarkRunRecord> SucceedClaimedPrimaryAsync(BenchmarkStore store, BenchmarkRunRecord run, Guid? judgeRevisionId)
    {
        var claimed = AssertEx.NotNull(await store.ClaimNextAsync());
        AssertEx.Equal(run.Id, claimed.RunId, "Each helper call claims exactly the run it just froze.");
        return await store.MarkPrimarySucceededAsync(new BenchmarkPrimarySuccessCommand
        {
            RunId = run.Id,
            ExpectedWorkVersion = claimed.Run.Version,
            OutputPartsJson = Encoding.UTF8.GetBytes("[{\"text\":\"answer\"}]"),
            LastStreamSequence = 1,
            EffectiveContextTokens = 4096,
            DurationMs = 10,
            TotalTokens = 5,
            TokensPerSecond = 500,
            PrimaryStopReason = "stop",
            JudgeAttempt = judgeRevisionId is { } revisionId ? JudgeSeed(revisionId) : null
        });
    }

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
}
