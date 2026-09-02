namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class DevelopmentStore
{
    public async Task<IReadOnlyList<DevelopmentEventSnapshot>> ListEventsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.DevelopmentEvents.AsNoTracking()
                               .Where(entity => entity.ProjectId == projectId)
                               .OrderBy(entity => entity.Sequence)
                               .Select(entity => new DevelopmentEventSnapshot(entity.Id,
                                   entity.ProjectId,
                                   entity.TaskId,
                                   entity.AttemptId,
                                   entity.Sequence,
                                   entity.EventType,
                                   entity.OccurredAtUtc,
                                   entity.OperationId,
                                   entity.OperationPhase,
                                   entity.Outcome))
                               .ToListAsync(cancellationToken)
                               .ConfigureAwait(false);
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
            await PreviousRoundFeedbackAsync(snapshot.Task.Id, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    ///     The reason the last request for changes on this task gave, so the next coder round is told what was wrong
    ///     with the last one instead of being asked for the same work again.
    ///     <para>
    ///         One row, off the task's own append-only log: the most recent event that CARRIES a reason and could have
    ///         moved the task, which is either the reviewer's <c>ReviewFinalized</c> or a workflow's own
    ///         <c>TaskTransitioned</c>. Its recorded status is then what decides — an event that carries a reason but
    ///         did not ask for rework (a block, a validation recovery) is not feedback and answers nothing.
    ///     </para>
    /// </summary>
    private async Task<string?> PreviousRoundFeedbackAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var latest = await _dbContext.DevelopmentEvents.AsNoTracking()
                                     .Where(entity => entity.TaskId == taskId
                                                      && entity.DetailJson != null
                                                      && (entity.EventType == "TaskTransitioned" || entity.EventType == "ReviewFinalized"))
                                     .OrderByDescending(entity => entity.Sequence)
                                     .FirstOrDefaultAsync(cancellationToken)
                                     .ConfigureAwait(false);
        if (latest is not { DetailJson: { } detail, ResultMetadataJson: { } metadata })
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DevelopmentOperationResult>(metadata, JsonOptions) is { Status: nameof(DevelopmentTaskStatus.ChangesRequested) }
                ? JsonSerializer.Deserialize<ReasonDetail>(detail, JsonOptions)?.Reason
                : null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // A row this build cannot read is a row with no feedback in it. The attempt still runs.
            return null;
        }
    }

    /// <summary>The one shape every reason-carrying event detail in this store is written in.</summary>
    private sealed record ReasonDetail(string? Reason);

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
