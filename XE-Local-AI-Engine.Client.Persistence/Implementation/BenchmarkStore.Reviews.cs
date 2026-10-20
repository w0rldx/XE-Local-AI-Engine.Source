namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class BenchmarkStore
{
    private const int MaxReviewPageSize = 200;
    private static readonly JsonSerializerOptions ReviewSerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<BenchmarkRunRecord> SubmitReviewAsync(BenchmarkReviewCommand command, CancellationToken cancellationToken = default)
    {
        ValidateReview(command);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Reserve SQLite's single writer with the run-version CAS before reading, as AcquireWorkCompletionAsync does: two racing reviewers serialize here and
        // the loser reads VersionConflict instead of a unique-index failure on a second review row.
        var reserved = await _dbContext.BenchmarkRuns
                                       .Where(entity => entity.Id == command.RunId && entity.Version == command.ExpectedRunVersion)
                                       .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.Version, entity => entity.Version), cancellationToken);
        var run = await RequireRunAsync(command.RunId, tracking: true, cancellationToken);
        if (reserved == 0)
        {
            throw new BenchmarkConflictException("VersionConflict");
        }

        await EnsureReviewableAsync(run, cancellationToken);

        var now = Now();
        var review = await _dbContext.BenchmarkRunReviews.SingleOrDefaultAsync(entity => entity.RunId == run.Id, cancellationToken);
        if (review is null)
        {
            review = new BenchmarkRunReview
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Version = 0,
                CreatedAtUtc = now
            };
            _dbContext.BenchmarkRunReviews.Add(review);
        }

        var approved = command.State == BenchmarkReviewStates.Approved;
        review.State = command.State;
        review.Score = approved ? command.Score : null;
        review.CriteriaScoresJson = command.Criteria is { Count: > 0 } criteria ? JsonSerializer.Serialize(criteria, ReviewSerializerOptions) : null;
        review.PolicyRevisionId = command.PolicyRevisionId;
        review.NotesJson = command.Notes is null ? null : JsonSerializer.SerializeToUtf8Bytes(command.Notes, ReviewSerializerOptions);
        review.Version++;
        review.UpdatedAtUtc = now;

        // The ranking reuses the existing user source: an approval IS the user score, a rejection clears it.
        run.UserScore = review.Score;
        run.Version++;
        run.UpdatedAtUtc = now;
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ToRecordWithJudgeAsync(run, cancellationToken);
    }

    public async Task<BenchmarkReviewQueuePage> ListReviewQueueAsync(BenchmarkReviewQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.State is not (BenchmarkReviewStates.Pending or BenchmarkReviewStates.Approved or BenchmarkReviewStates.Rejected or BenchmarkReviewStates.Stale))
        {
            throw new BenchmarkValidationException("Review state must be pending, approved, rejected or stale.");
        }

        if (query.Skip < 0 || query.Take is < 1 or > MaxReviewPageSize)
        {
            throw new BenchmarkValidationException($"Review paging must have skip >= 0 and 1 <= take <= {MaxReviewPageSize}.");
        }

        // Flat columns only. A row is reviewable exactly when SubmitReviewAsync would accept it — succeeded, not a warm-up, a scoring item (or none) of a
        // review project — and its identity stamps still match what the project asks now, because a revised item is excluded before any review matters.
        var projectId = query.ProjectId;
        var rows = from run in _dbContext.BenchmarkRuns.AsNoTracking()
                   join project in _dbContext.BenchmarkProjects.AsNoTracking() on run.ProjectId equals project.Id
                   where project.RequiresReview
                         && run.PrimaryStatus == BenchmarkPrimaryStatus.Succeeded
                         && !run.IsWarmup
                         && (projectId == null || run.ProjectId == projectId)
                         && run.TaskItemSetHash == (project.TaskItemSetHash ?? LegacyTaskHash)
                         && (run.TaskItemId == null
                             || _dbContext.BenchmarkTaskItems.Any(item => item.Id == run.TaskItemId
                                                                         && item.Kind != BenchmarkTaskItemKinds.Niah
                                                                         && item.CountsTowardScore
                                                                         && item.InputHash == run.TaskInputHash))
                   join review in _dbContext.BenchmarkRunReviews.AsNoTracking() on run.Id equals review.RunId into reviews
                   from review in reviews.DefaultIfEmpty()
                   select new
                   {
                       run.Id,
                       run.ProjectId,
                       run.Version,
                       run.PrimaryModelName,
                       run.TaskItemId,
                       run.TaskItemIndex,
                       run.PrimaryCompletedAtUtc,
                       ReviewState = review == null ? null : review.State,
                       ReviewScore = review == null ? null : review.Score,
                       ReviewedAtUtc = review == null ? (long?)null : review.UpdatedAtUtc,
                       Stale = review != null && review.PolicyRevisionId != project.CurrentJudgePolicyRevisionId,
                       JudgingCurrent = project.CurrentJudgePolicyRevisionId == null
                                        || _dbContext.BenchmarkJudgeAttempts.Any(attempt => attempt.Id == run.CurrentJudgeAttemptId
                                                                                           && attempt.Status == BenchmarkJudgeAttemptStatus.Succeeded
                                                                                           && attempt.PolicyRevisionId == project.CurrentJudgePolicyRevisionId)
                   };

        // A run still waiting on its judging under the current revision is listed once that judging succeeds: the review service refuses it until then.
        rows = query.State switch
        {
            BenchmarkReviewStates.Pending => rows.Where(row => (row.ReviewState == null || row.Stale) && row.JudgingCurrent),
            BenchmarkReviewStates.Stale => rows.Where(row => row.ReviewState != null && row.Stale && row.JudgingCurrent),
            _ => rows.Where(row => row.ReviewState == query.State && !row.Stale)
        };

        var total = await rows.CountAsync(cancellationToken);
        var page = await rows.OrderBy(row => row.PrimaryCompletedAtUtc)
                             .ThenBy(row => row.Id)
                             .Skip(query.Skip)
                             .Take(query.Take)
                             .ToArrayAsync(cancellationToken);
        return new BenchmarkReviewQueuePage
        {
            Items = [.. page.Select(row => new BenchmarkReviewQueueEntry
            {
                RunId = row.Id,
                ProjectId = row.ProjectId,
                RunVersion = row.Version,
                PrimaryModelName = row.PrimaryModelName,
                TaskItemId = row.TaskItemId,
                TaskItemIndex = row.TaskItemIndex,
                PrimaryCompletedAtUtc = row.PrimaryCompletedAtUtc,
                State = (row.ReviewState, row.Stale) switch
                {
                    (null, _) => BenchmarkReviewStates.Pending,
                    (_, true) => BenchmarkReviewStates.Stale,
                    var (state, _) => state
                },
                ReviewScore = row.ReviewScore,
                ReviewedAtUtc = row.ReviewedAtUtc
            })],
            TotalCount = total
        };
    }

    private async Task<BenchmarkRunReviewRecord?> LoadReviewAsync(Guid runId, CancellationToken cancellationToken) =>
        await _dbContext.BenchmarkRunReviews.AsNoTracking().SingleOrDefaultAsync(entity => entity.RunId == runId, cancellationToken) is { } review
            ? ToRecord(review)
            : null;

    /// <summary>The comparison <see cref="LoadReviewGatesAsync" /> makes for staleness; <see langword="null" /> when there is no review.</summary>
    private async Task<bool?> LoadReviewPolicyCurrentAsync(Guid projectId, BenchmarkRunReviewRecord? review, CancellationToken cancellationToken)
    {
        if (review is null)
        {
            return null;
        }

        var current = await _dbContext.BenchmarkProjects.AsNoTracking()
                                      .Where(entity => entity.Id == projectId)
                                      .Select(static entity => entity.CurrentJudgePolicyRevisionId)
                                      .SingleOrDefaultAsync(cancellationToken);
        return review.PolicyRevisionId == current;
    }

    /// <summary>Everything about the run the store can check; the rubric and the judging outcome are the application layer's.</summary>
    private async Task EnsureReviewableAsync(BenchmarkRun run, CancellationToken cancellationToken)
    {
        var requiresReview = await _dbContext.BenchmarkProjects.AsNoTracking()
                                             .Where(entity => entity.Id == run.ProjectId)
                                             .Select(static entity => entity.RequiresReview)
                                             .SingleOrDefaultAsync(cancellationToken);
        if (!requiresReview || run.PrimaryStatus != BenchmarkPrimaryStatus.Succeeded || run.IsWarmup)
        {
            throw new BenchmarkConflictException("NotReviewable");
        }

        // A run that names no item was frozen before task suites and counts as scoring. A non-scoring leaf (a NIAH case) keeps its judge path for the recall axis.
        if (run.TaskItemId is { } itemId)
        {
            var item = await _dbContext.BenchmarkTaskItems.AsNoTracking()
                                       .Where(entity => entity.Id == itemId)
                                       .Select(static entity => new
                                       {
                                           entity.Kind,
                                           entity.CountsTowardScore
                                       })
                                       .SingleOrDefaultAsync(cancellationToken);
            if (item is null || !BenchmarkTaskItemKinds.IsLeaf(item.Kind) || !item.CountsTowardScore)
            {
                throw new BenchmarkConflictException("NotReviewable");
            }
        }

        // An answer to a since-edited question or suite never ranks, whatever a review says; the inbox already leaves it out.
        var identity = await LoadRunIdentityAsync(run, cancellationToken);
        if (identity.Revised || identity.SetRevised)
        {
            throw new BenchmarkConflictException("NotReviewable");
        }
    }

    private static void ValidateReview(BenchmarkReviewCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.State is not (BenchmarkReviewStates.Approved or BenchmarkReviewStates.Rejected))
        {
            throw new BenchmarkValidationException("A review decision must be approved or rejected.");
        }

        if (command.State == BenchmarkReviewStates.Approved && command.Score is not (>= 0 and <= 100))
        {
            throw new BenchmarkValidationException("An approval needs an overall score between 0 and 100.");
        }

        if (command.Notes is { Length: > BenchmarkReviewLimits.MaxNotesCharacters })
        {
            throw new BenchmarkValidationException($"Review notes must be at most {BenchmarkReviewLimits.MaxNotesCharacters} characters.");
        }

        if (command.Criteria is { } criteria
            && (criteria.Any(static criterion => string.IsNullOrWhiteSpace(criterion.CriterionId) || criterion.Score is < 0 or > 10)
                || criteria.Select(static criterion => criterion.CriterionId).Distinct(StringComparer.Ordinal).Count() != criteria.Count))
        {
            throw new BenchmarkValidationException("Each criterion is scored once, between 0 and 10.");
        }
    }

    private static BenchmarkRunReviewRecord ToRecord(BenchmarkRunReview entity) =>
        new()
        {
            Id = entity.Id,
            RunId = entity.RunId,
            State = entity.State,
            Score = entity.Score,
            Criteria = entity.CriteriaScoresJson is null
                ? []
                : JsonSerializer.Deserialize<BenchmarkReviewCriterionScore[]>(entity.CriteriaScoresJson, ReviewSerializerOptions) ?? [],
            PolicyRevisionId = entity.PolicyRevisionId,
            Notes = entity.NotesJson is null ? null : JsonSerializer.Deserialize<string>(entity.NotesJson, ReviewSerializerOptions),
            Version = entity.Version,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc
        };
}
