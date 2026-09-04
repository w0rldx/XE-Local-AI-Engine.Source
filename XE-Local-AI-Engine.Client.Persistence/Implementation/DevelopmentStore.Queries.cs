namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

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
                                     .ToListAsync(cancellationToken)
                                     .ConfigureAwait(false);
        return events.Select(static entity => new DevelopmentEventSnapshot(entity.Id,
                         entity.ProjectId,
                         entity.TaskId,
                         entity.AttemptId,
                         entity.Sequence,
                         entity.EventType,
                         entity.OccurredAtUtc,
                         entity.OperationId,
                         entity.OperationPhase,
                         entity.Outcome,
                         ReasonOf(entity.DetailJson)))
                     .ToArray();
    }

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
                             .ConfigureAwait(false)
                       ?? throw new KeyNotFoundException($"Development attempt '{attemptId}' was not found.");

        return new DevelopmentExecutionSnapshot(snapshot.Project.Id,
            snapshot.Task.Id,
            snapshot.Attempt.Id,
            snapshot.Project.SelectedFolderId,
            snapshot.Project.RepositoryIdentityHash,
            snapshot.Project.BaseBranch,
            snapshot.Project.EgressPolicy,
            snapshot.Project.ConfigurationVersion,
            snapshot.Project.TrustedRepositoryAcknowledged,
            snapshot.Project.TrustedRepositoryPolicyVersion,
            snapshot.Project.TrustedRepositoryAcknowledgedAtUtc,
            snapshot.Project.MaxTokens,
            snapshot.Project.MaxDurationSeconds,
            Encoding.UTF8.GetString(snapshot.Task.Title),
            Encoding.UTF8.GetString(snapshot.Task.Requirements),
            Encoding.UTF8.GetString(snapshot.Task.AcceptanceCriteriaJson),
            snapshot.Task.Status,
            snapshot.Task.Version,
            snapshot.Attempt.Role,
            snapshot.Attempt.Status,
            snapshot.Attempt.ModelId,
            snapshot.Attempt.Provider,
            snapshot.Attempt.Version,

            // The attempt's own immutable snapshot wins. Falling back to the project only when the attempt has none
            // keeps attempts that predate the column behaving exactly as before, and lets a project whose profile was
            // backfilled after the attempt started still resolve one.
            snapshot.Attempt.CommandProfileJson ?? snapshot.Project.CommandProfileJson,
            await PreviousRoundFeedbackAsync(snapshot.Task.Id, cancellationToken).ConfigureAwait(false),
            await WorkflowPolicyTextAsync(snapshot.Task.Id, cancellationToken).ConfigureAwait(false),
            await OperatorInstructionAsync(snapshot.Task.Id, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    ///     The reason the last request for changes on this task gave, so the next coder round is told what was wrong
    ///     with the last one instead of being asked for the same work again.
    ///     <para>
    ///         One row, off the task's own append-only log: the LATEST event that carries a reason, whichever of the
    ///         three writes it — the reviewer's <c>ReviewFinalized</c>, the deterministic gate's
    ///         <c>ValidationFinalized</c>, or a workflow's own <c>TaskTransitioned</c>. Latest rather than
    ///         reviewer-first, because a validation failure AFTER a reviewer's round is the newer fact, and answering
    ///         with the reviewer's sentence would replay round N-1's complaint over a round the gate has since judged.
    ///     </para>
    ///     <para>
    ///         The recorded status is what qualifies it: only an event that left the task where a coder round runs is
    ///         feedback for one. A block, an apply, and a validation that PASSED all carry reasons and none of them are.
    ///     </para>
    ///     <para>
    ///         A transition a PERSON asked for is excluded, because it answers <see cref="OperatorInstructionAsync" />
    ///         instead. The two are disjoint by construction so a prompt that ranks the operator above the reviewer
    ///         cannot render the same sentence twice, once under each heading.
    ///     </para>
    /// </summary>
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
                                     .FirstOrDefaultAsync(cancellationToken)
                                     .ConfigureAwait(false);
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
    ///     The rule-set text a workflow injected onto this task, or nothing when no workflow drives it.
    ///     <para>
    ///         One row, off the task's own append-only log, exactly as <see cref="PreviousRoundFeedbackAsync" /> reads
    ///         its sentence — so it costs no migration and no column, and a task nothing injected policy onto answers
    ///         null rather than empty. LATEST wins: a node run re-bound to the same task records again, and the newer
    ///         resolution is the one that governs the round about to run.
    ///     </para>
    ///     <para>
    ///         A latest row with BLANK text is the workflow saying nothing applies any more, and answers null. That is
    ///         what bounds the snapshot in time: the executor records on EVERY dispatch — an empty resolution
    ///         included — and again when it settles the node run, so neither a workflow that resolved no policy nor one
    ///         that has finished governs the rounds that come after it.
    ///     </para>
    /// </summary>
    private async Task<string?> WorkflowPolicyTextAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var latest = await _dbContext.DevelopmentEvents.AsNoTracking()
                                     .Where(entity => entity.TaskId == taskId && entity.EventType == "WorkflowPolicyApplied" && entity.DetailJson != null)
                                     .OrderByDescending(entity => entity.Sequence)
                                     .FirstOrDefaultAsync(cancellationToken)
                                     .ConfigureAwait(false);
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
    ///     <para>
    ///         No STATUS gate, which is what separates it from <see cref="PreviousRoundFeedbackAsync" />. A Dev Mode
    ///         task's requirements cannot be edited, so an operator's retry reason is the only channel that can amend
    ///         one — and an amendment that stopped governing at the next event would be undone by the reviewer round it
    ///         was written to correct.
    ///     </para>
    ///     <para>
    ///         It is bounded the same way <see cref="WorkflowPolicyTextAsync" /> is, and by the same row: only an
    ///         instruction written AFTER the latest <c>WorkflowPolicyApplied</c> governs. The executor writes one on
    ///         every dispatch and again when it settles the node run, so an instruction dies with the node-run attempt
    ///         that carried it and cannot outlive the workflow into a second node run or an operator's own later
    ///         rounds. A task no workflow ever drove has no such row, no boundary, and keeps the unbounded reading.
    ///     </para>
    ///     <para>
    ///         ponytail: the ceiling is one instruction per dispatch — a later comment-carrying Retry writes a new
    ///         boundary and a new instruction after it, but nothing retracts one WITHIN the dispatch that wrote it. If
    ///         that ever bites, read a blank-reason operator row as the retraction.
    ///     </para>
    /// </summary>
    private async Task<string?> OperatorInstructionAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var boundary = await _dbContext.DevelopmentEvents.AsNoTracking()
                                       .Where(entity => entity.TaskId == taskId && entity.EventType == "WorkflowPolicyApplied")
                                       .OrderByDescending(entity => entity.Sequence)
                                       .Select(entity => (long?)entity.Sequence)
                                       .FirstOrDefaultAsync(cancellationToken)
                                       .ConfigureAwait(false) ?? 0L;
        var latest = await _dbContext.DevelopmentEvents.AsNoTracking()
                                     .Where(entity => entity.TaskId == taskId
                                                      && entity.Sequence > boundary
                                                      && entity.EventType == "TaskTransitioned"
                                                      && entity.Outcome == OperatorTransitionOutcome
                                                      && entity.DetailJson != null)
                                     .OrderByDescending(entity => entity.Sequence)
                                     .FirstOrDefaultAsync(cancellationToken)
                                     .ConfigureAwait(false);
        var said = ReasonOf(latest?.DetailJson);
        return string.IsNullOrWhiteSpace(said) ? null : said;
    }

    public async Task<IReadOnlyList<DevelopmentProjectSnapshot>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var projects = await _dbContext.DevelopmentProjects.AsNoTracking()
                                       .OrderByDescending(entity => entity.UpdatedAtUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);
        return projects.Select(ProjectSnapshot).ToArray();
    }

    public async Task<DevelopmentProjectSnapshot> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _dbContext.DevelopmentProjects.AsNoTracking()
                                      .SingleOrDefaultAsync(entity => entity.Id == projectId, cancellationToken)
                                      .ConfigureAwait(false)
                      ?? throw new KeyNotFoundException($"Development project '{projectId}' was not found.");
        return ProjectSnapshot(project);
    }

    public async Task<DevelopmentProjectSnapshot> ReconnectProjectRepositoryAsync(Guid projectId,
        Guid selectedFolderId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var project = await _dbContext.DevelopmentProjects
                                      .SingleOrDefaultAsync(entity => entity.Id == projectId, cancellationToken)
                                      .ConfigureAwait(false)
                      ?? throw new KeyNotFoundException($"Development project '{projectId}' was not found.");
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
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new DevelopmentConcurrencyException("The Development project changed before its repository could be reconnected.", exception);
        }

        return ProjectSnapshot(project);
    }

    public async Task<DevelopmentProjectSnapshot> BackfillCommandProfileAsync(Guid projectId,
        string commandProfileJson,
        CancellationToken cancellationToken = default)
    {
        EnsureNotBlank(commandProfileJson, nameof(commandProfileJson));
        var project = await _dbContext.DevelopmentProjects
                                      .SingleOrDefaultAsync(entity => entity.Id == projectId, cancellationToken)
                                      .ConfigureAwait(false)
                      ?? throw new KeyNotFoundException($"Development project '{projectId}' was not found.");

        // Fill-only. An existing profile is the operator-confirmed agreement for the life of the project, so a backfill
        // pass must return it untouched rather than replace it — this is what makes a second pass, or two racing
        // passes, harmless.
        if (!string.IsNullOrWhiteSpace(project.CommandProfileJson))
        {
            return ProjectSnapshot(project);
        }

        project.CommandProfileJson = commandProfileJson;
        project.ConfigurationVersion++;
        project.UpdatedAtUtc = Now();
        project.Version++;
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new DevelopmentConcurrencyException("The Development project changed before its command profile could be backfilled.", exception);
        }

        return ProjectSnapshot(project);
    }

    public async Task<DevelopmentTaskSnapshot> GetTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await _dbContext.DevelopmentTasks.AsNoTracking()
                                   .SingleOrDefaultAsync(entity => entity.Id == taskId, cancellationToken)
                                   .ConfigureAwait(false)
                   ?? throw new KeyNotFoundException($"Development task '{taskId}' was not found.");
        return TaskSnapshot(task);
    }

    public async Task<IReadOnlyList<DevelopmentTaskSnapshot>> ListTasksAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var tasks = await _dbContext.DevelopmentTasks.AsNoTracking()
                                    .Where(entity => entity.ProjectId == projectId)
                                    .OrderBy(entity => entity.CreatedAtUtc)
                                    .ThenBy(entity => entity.Id)
                                    .ToListAsync(cancellationToken)
                                    .ConfigureAwait(false);
        return tasks.Select(TaskSnapshot).ToArray();
    }

    public async Task<IReadOnlyList<DevelopmentAttemptSnapshot>> ListAttemptsAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.DevelopmentAttempts.AsNoTracking()
                               .Where(entity => entity.TaskId == taskId)
                               .OrderBy(entity => entity.StartedAtUtc)
                               .ThenBy(entity => entity.Id)
                               .Select(entity => new DevelopmentAttemptSnapshot(entity.Id,
                                   entity.TaskId,
                                   entity.PredecessorAttemptId,
                                   entity.Role,
                                   entity.ModelId,
                                   entity.Provider,
                                   entity.Status,
                                   entity.StartedAtUtc,
                                   entity.EndedAtUtc,
                                   entity.TerminalReason,
                                   entity.InputTokens,
                                   entity.OutputTokens,
                                   entity.Version))
                               .ToListAsync(cancellationToken)
                               .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DevelopmentArtifactSnapshot>> ListArtifactsAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var artifacts = await _dbContext.DevelopmentArtifacts.AsNoTracking()
                                        .Where(entity => entity.TaskId == taskId)
                                        .OrderBy(entity => entity.CreatedAtUtc)
                                        .ThenBy(entity => entity.Id)
                                        .ToListAsync(cancellationToken)
                                        .ConfigureAwait(false);
        return artifacts.Select(ArtifactSnapshot).ToArray();
    }

    public async Task<DevelopmentArtifactSnapshot> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        var artifact = await _dbContext.DevelopmentArtifacts.AsNoTracking()
                                       .SingleOrDefaultAsync(entity => entity.Id == artifactId, cancellationToken)
                                       .ConfigureAwait(false)
                       ?? throw new KeyNotFoundException($"Development artifact '{artifactId}' was not found.");
        return ArtifactSnapshot(artifact);
    }
}
