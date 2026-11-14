namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class DevelopmentStore
{
    /// <summary>
    ///     The outcome a task transition carries when a PERSON asked for it, written by
    ///     <see cref="TransitionTaskAsync" /> and read by <see cref="OperatorInstructionAsync" />. Any other transition
    ///     keeps the plain "Transitioned" it always had.
    /// </summary>
    private const string OperatorTransitionOutcome = "TransitionedByOperator";

    public async Task<IReadOnlyList<DevelopmentEventSnapshot>> ListEventsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        // Materialized rather than projected, because the reason lives in an ENCRYPTED column: the decryption
        // interceptor runs on materialization, and a projection straight to the bytes would hand back ciphertext.
        var events = await _dbContext.DevelopmentEvents.AsNoTracking()
                                     .Where(entity => entity.ProjectId == projectId)
                                     .OrderBy(entity => entity.Sequence)
                                     .ToListAsync(cancellationToken);
        return events.Select(EventSnapshot).ToArray();
    }

    public async Task<IReadOnlyList<DevelopmentEventSnapshot>> ListLatestEventsAsync(Guid projectId,
        int limitPerTask,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limitPerTask, 1);

        // Two statements whatever the project holds. The cap is chosen over plaintext keys, so a row it drops is never
        // decrypted; per task, so a busy sibling cannot push the selected task's history out of the window.
        var keys = await _dbContext.DevelopmentEvents.AsNoTracking()
                                   .Where(entity => entity.ProjectId == projectId)
                                   .Select(entity => new
                                   {
                                       entity.Id,
                                       entity.TaskId,
                                       entity.Sequence
                                   })
                                   .ToListAsync(cancellationToken);
        Guid[] kept =
        [
            .. keys.GroupBy(static key => key.TaskId)
                   .SelectMany(group => group.OrderByDescending(static key => key.Sequence).Take(limitPerTask))
                   .Select(static key => key.Id)
        ];
        var events = await _dbContext.DevelopmentEvents.AsNoTracking()
                                     // ONE JSON parameter (json_each), not one per id: a long ledger exceeds SQLite's variable limit.
                                     .Where(entity => EF.Parameter(kept).Contains(entity.Id))
                                     .OrderBy(entity => entity.Sequence)
                                     .ToListAsync(cancellationToken);
        return events.Select(EventSnapshot).ToArray();
    }

    private static DevelopmentEventSnapshot EventSnapshot(DevelopmentEvent entity) =>
        new()
        {
            Id = entity.Id,
            ProjectId = entity.ProjectId,
            TaskId = entity.TaskId,
            AttemptId = entity.AttemptId,
            Sequence = entity.Sequence,
            EventType = entity.EventType,
            OccurredAtUtc = entity.OccurredAtUtc,
            OperationId = entity.OperationId,
            OperationPhase = entity.OperationPhase,
            Outcome = entity.Outcome,
            Reason = ReasonOf(entity.DetailJson)
        };

    public async Task<DevelopmentExecutionSnapshot> GetExecutionSnapshotAsync(Guid attemptId, CancellationToken cancellationToken = default)
    {
        var snapshot = await (from attempt in _dbContext.DevelopmentAttempts.AsNoTracking()
                               join task in _dbContext.DevelopmentTasks.AsNoTracking() on attempt.TaskId equals task.Id
                               join project in _dbContext.DevelopmentProjects.AsNoTracking() on task.ProjectId equals project.Id
                               where attempt.Id == attemptId
                               select new
                               {
                                   Project = project,
                                   Task = task,
                                   Attempt = attempt
                               })
                           .SingleOrDefaultAsync(cancellationToken)
                       ?? throw new DevelopmentNotFoundException($"Development attempt '{attemptId}' was not found.");

        return new DevelopmentExecutionSnapshot
        {
            ProjectId = snapshot.Project.Id,
            TaskId = snapshot.Task.Id,
            AttemptId = snapshot.Attempt.Id,
            SelectedFolderId = snapshot.Project.SelectedFolderId,
            RepositoryIdentityHash = snapshot.Project.RepositoryIdentityHash,
            BaseBranch = snapshot.Project.BaseBranch,
            EgressPolicy = snapshot.Project.EgressPolicy,
            ConfigurationVersion = snapshot.Project.ConfigurationVersion,
            TrustedRepositoryAcknowledged = snapshot.Project.TrustedRepositoryAcknowledged,
            TrustedRepositoryPolicyVersion = snapshot.Project.TrustedRepositoryPolicyVersion,
            TrustedRepositoryAcknowledgedAtUtc = snapshot.Project.TrustedRepositoryAcknowledgedAtUtc,
            MaxTokens = snapshot.Project.MaxTokens,
            MaxDurationSeconds = snapshot.Project.MaxDurationSeconds,
            Title = Encoding.UTF8.GetString(snapshot.Task.Title),
            Requirements = Encoding.UTF8.GetString(snapshot.Task.Requirements),
            AcceptanceCriteriaJson = Encoding.UTF8.GetString(snapshot.Task.AcceptanceCriteriaJson),
            TaskStatus = snapshot.Task.Status,
            TaskVersion = snapshot.Task.Version,
            AttemptRole = snapshot.Attempt.Role,
            AttemptStatus = snapshot.Attempt.Status,
            ModelId = snapshot.Attempt.ModelId,
            Provider = snapshot.Attempt.Provider,
            AttemptVersion = snapshot.Attempt.Version,
            // The attempt's own immutable snapshot wins. Falling back to the project only when the attempt has none keeps attempts that predate the column behaving
            // exactly as before, and lets a project whose profile was backfilled after the attempt started still resolve one.
            CommandProfileJson = snapshot.Attempt.CommandProfileJson ?? snapshot.Project.CommandProfileJson,
            PreviousRoundFeedback = await PreviousRoundFeedbackAsync(snapshot.Task.Id, cancellationToken),
            WorkflowPolicyText = await WorkflowPolicyTextAsync(snapshot.Task.Id, cancellationToken),
            OperatorInstruction = await OperatorInstructionAsync(snapshot.Task.Id, cancellationToken)
        };
    }

    /// <summary>
    ///     The reason the last request for changes on this task gave, so the next coder round is told what was wrong
    ///     with the last one instead of being asked for the same work again.
    /// </summary>
    /// <remarks>
    ///     One row off the task's append-only log: the LATEST event carrying a reason, from the reviewer's
    ///     <c>ReviewFinalized</c>, the gate's <c>ValidationFinalized</c> or a <c>TaskTransitioned</c>.
    ///     Latest, because a validation failure after a reviewer's round is the newer fact. Only an event that left the
    ///     task where a coder round runs qualifies, and a transition a PERSON asked for is excluded: that answers
    ///     <see cref="OperatorInstructionAsync" />, disjoint so no prompt renders the same sentence twice.
    /// </remarks>
    private async Task<string?> PreviousRoundFeedbackAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var latest = await _dbContext.DevelopmentEvents.AsNoTracking()
                                     .Where(entity => entity.TaskId == taskId
                                                      && entity.DetailJson != null
                                                      && entity.Outcome != OperatorTransitionOutcome
                                                      && (entity.EventType == "TaskTransitioned"
                                                          || entity.EventType == "ReviewFinalized"
                                                          || entity.EventType == "ValidationFinalized"))
                                     .OrderByDescending(entity => entity.Sequence)
                                     .FirstOrDefaultAsync(cancellationToken);
        if (latest is not { DetailJson: { } detail, ResultMetadataJson: { } metadata })
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DevelopmentOperationResult>(metadata, JsonOptions)
                is { Status: nameof(DevelopmentTaskStatus.ChangesRequested) or nameof(DevelopmentTaskStatus.InProgress) }
                ? ReasonOf(detail)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // A row this build cannot read is a row with no feedback in it. The attempt still runs.
            return null;
        }
    }

    /// <summary>
    ///     The sentence an event detail carries, or nothing. Read with the store's own options, which are
    ///     case-insensitive — so the PascalCase rows written before the store re-cased its documents still answer.
    /// </summary>
    private static string? ReasonOf(byte[]? detailJson)
    {
        if (detailJson is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ReasonDetail>(detailJson, JsonOptions)?.Reason;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // A detail shape this build cannot read is one with no reason in it. The event still lists.
            return null;
        }
    }

    /// <summary>The one shape every reason-carrying event detail in this store is written in.</summary>
    private sealed record ReasonDetail(string? Reason);

    /// <summary>
    ///     The instructions a Graph workflow <c>DevTask</c> node recorded on this task, or nothing when no node run drives it.
    /// </summary>
    /// <remarks>
    ///     One row off the task's append-only log, exactly as <see cref="PreviousRoundFeedbackAsync" /> reads its
    ///     sentence, so it costs no migration and no column. LATEST wins: a node run re-bound to the same task records
    ///     again. A latest row with BLANK text says nothing applies any more and answers null, which is what bounds
    ///     the snapshot in time — the lane records when each attempt starts and again when it ends.
    /// </remarks>
    private async Task<string?> WorkflowPolicyTextAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var latest = await _dbContext.DevelopmentEvents.AsNoTracking()
                                     .Where(entity => entity.TaskId == taskId && entity.EventType == "WorkflowPolicyApplied" && entity.DetailJson != null)
                                     .OrderByDescending(entity => entity.Sequence)
                                     .FirstOrDefaultAsync(cancellationToken);
        if (latest?.DetailJson is not { Length: > 0 } detail)
        {
            return null;
        }

        try
        {
            var policyText = JsonSerializer.Deserialize<WorkflowPolicyDetail>(detail, JsonOptions)?.PolicyText;
            return string.IsNullOrWhiteSpace(policyText) ? null : policyText;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // A row this build cannot read is a row with no policy in it. The attempt still runs, ungoverned but
            // honest about it — failing a coder round over an unreadable audit row would be the worse answer.
            return null;
        }
    }

    /// <summary>The shape a workflow's policy event carries: the rendered text, and which rule sets composed it.</summary>
    private sealed record WorkflowPolicyDetail(string? PolicyText, IReadOnlyList<DevelopmentWorkflowRuleSetReference>? RuleSets);

    /// <summary>
    ///     The last thing a PERSON told this task to do differently: the latest transition whose outcome is
    ///     <see cref="OperatorTransitionOutcome" />, and nothing when nobody has ever asked.
    /// </summary>
    /// <remarks>
    ///     No STATUS gate, which separates it from <see cref="PreviousRoundFeedbackAsync" />: a Dev Mode task's
    ///     requirements cannot be edited, so an operator's retry reason is the only channel that can amend one. Only an
    ///     instruction written AFTER the latest <c>WorkflowPolicyApplied</c> governs, so it dies with the node-run
    ///     attempt; a task no workflow drove has no such row and keeps the unbounded reading. A BLANK-reason operator
    ///     row is the retraction, so there is no <c>DetailJson</c> filter — <see cref="ReasonOf" /> answers null for it.
    /// </remarks>
    private async Task<string?> OperatorInstructionAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var boundary = await _dbContext.DevelopmentEvents.AsNoTracking()
                                       .Where(entity => entity.TaskId == taskId && entity.EventType == "WorkflowPolicyApplied")
                                       .OrderByDescending(entity => entity.Sequence)
                                       .Select(entity => (long?)entity.Sequence)
                                       .FirstOrDefaultAsync(cancellationToken) ?? 0L;
        var latest = await _dbContext.DevelopmentEvents.AsNoTracking()
                                     .Where(entity => entity.TaskId == taskId
                                                      && entity.Sequence > boundary
                                                      && entity.EventType == "TaskTransitioned"
                                                      && entity.Outcome == OperatorTransitionOutcome)
                                     .OrderByDescending(entity => entity.Sequence)
                                     .FirstOrDefaultAsync(cancellationToken);
        var said = ReasonOf(latest?.DetailJson);
        return string.IsNullOrWhiteSpace(said) ? null : said;
    }

    public async Task<IReadOnlyList<DevelopmentProjectSnapshot>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var projects = await _dbContext.DevelopmentProjects.AsNoTracking()
                                       .OrderByDescending(entity => entity.UpdatedAtUtc)
                                       .ToListAsync(cancellationToken);
        return projects.Select(ProjectSnapshot).ToArray();
    }

    public async Task<DevelopmentProjectSnapshot> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _dbContext.DevelopmentProjects.AsNoTracking()
                                      .SingleOrDefaultAsync(entity => entity.Id == projectId, cancellationToken)
                      ?? throw new DevelopmentNotFoundException($"Development project '{projectId}' was not found.");
        return ProjectSnapshot(project);
    }

    public async Task<DevelopmentProjectSnapshot> ReconnectProjectRepositoryAsync(Guid projectId,
        Guid selectedFolderId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var project = await _dbContext.DevelopmentProjects
                                      .SingleOrDefaultAsync(entity => entity.Id == projectId, cancellationToken)
                      ?? throw new DevelopmentNotFoundException($"Development project '{projectId}' was not found.");
        if (project.SelectedFolderId == selectedFolderId)
        {
            return ProjectSnapshot(project);
        }

        if (project.SelectedFolderId is not null)
        {
            throw new DevelopmentConcurrencyException("The Development project is already connected to another selected folder.");
        }

        EnsureVersion(project.Version, expectedVersion, "project");
        project.SelectedFolderId = selectedFolderId;
        project.UpdatedAtUtc = Now();
        project.Version++;
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new DevelopmentConcurrencyException("The Development project changed before its repository could be reconnected.", exception);
        }

        return ProjectSnapshot(project);
    }

    public async Task<DevelopmentTaskSnapshot> GetTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await _dbContext.DevelopmentTasks.AsNoTracking()
                                   .SingleOrDefaultAsync(entity => entity.Id == taskId, cancellationToken)
                   ?? throw new DevelopmentNotFoundException($"Development task '{taskId}' was not found.");
        return TaskSnapshot(task);
    }

    public async Task<IReadOnlyList<DevelopmentTaskSnapshot>> ListTasksAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var tasks = await _dbContext.DevelopmentTasks.AsNoTracking()
                                    .Where(entity => entity.ProjectId == projectId)
                                    .OrderBy(entity => entity.CreatedAtUtc)
                                    .ThenBy(entity => entity.Id)
                                    .ToListAsync(cancellationToken);
        return tasks.Select(TaskSnapshot).ToArray();
    }

    public Task<IReadOnlyList<DevelopmentAttemptSnapshot>> ListAttemptsAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        ListAttemptsWhereAsync(entity => entity.TaskId == taskId, cancellationToken);

    public Task<IReadOnlyList<DevelopmentAttemptSnapshot>> ListAttemptsForTasksAsync(IReadOnlyCollection<Guid> taskIds,
        CancellationToken cancellationToken = default) =>
        ListAttemptsWhereAsync(entity => taskIds.Contains(entity.TaskId), cancellationToken);

    public Task<IReadOnlyList<DevelopmentArtifactSnapshot>> ListArtifactsAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        ListArtifactsWhereAsync(entity => entity.TaskId == taskId, cancellationToken);

    public Task<IReadOnlyList<DevelopmentArtifactSnapshot>> ListArtifactsForTasksAsync(IReadOnlyCollection<Guid> taskIds,
        CancellationToken cancellationToken = default) =>
        ListArtifactsWhereAsync(entity => taskIds.Contains(entity.TaskId), cancellationToken);

    private async Task<IReadOnlyList<DevelopmentAttemptSnapshot>> ListAttemptsWhereAsync(Expression<Func<DevelopmentAttempt, bool>> predicate,
        CancellationToken cancellationToken)
    {
        return await _dbContext.DevelopmentAttempts.AsNoTracking()
                               .Where(predicate)
                               .OrderBy(entity => entity.StartedAtUtc)
                               .ThenBy(entity => entity.Id)
                               .Select(entity => new DevelopmentAttemptSnapshot
                               {
                                   Id = entity.Id,
                                   TaskId = entity.TaskId,
                                   PredecessorAttemptId = entity.PredecessorAttemptId,
                                   Role = entity.Role,
                                   ModelId = entity.ModelId,
                                   Provider = entity.Provider,
                                   Status = entity.Status,
                                   StartedAtUtc = entity.StartedAtUtc,
                                   EndedAtUtc = entity.EndedAtUtc,
                                   TerminalReason = entity.TerminalReason,
                                   InputTokens = entity.InputTokens,
                                   OutputTokens = entity.OutputTokens,
                                   Version = entity.Version
                               })
                               .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<DevelopmentArtifactSnapshot>> ListArtifactsWhereAsync(Expression<Func<DevelopmentArtifact, bool>> predicate,
        CancellationToken cancellationToken)
    {
        var artifacts = await _dbContext.DevelopmentArtifacts.AsNoTracking()
                                        .Where(predicate)
                                        .OrderBy(entity => entity.CreatedAtUtc)
                                        .ThenBy(entity => entity.Id)
                                        .ToListAsync(cancellationToken);
        return artifacts.Select(ArtifactSnapshot).ToArray();
    }

    public async Task<DevelopmentArtifactSnapshot> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        var artifact = await _dbContext.DevelopmentArtifacts.AsNoTracking()
                                       .SingleOrDefaultAsync(entity => entity.Id == artifactId, cancellationToken)
                       ?? throw new DevelopmentNotFoundException($"Development artifact '{artifactId}' was not found.");
        return ArtifactSnapshot(artifact);
    }
}
