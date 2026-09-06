namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class DevelopmentStore
{
    public async Task<DevelopmentOperationResult> AttachArtifactAsync(DevelopmentAttachArtifactCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateArtifactCommand(command);

        return await ExecuteOperationAsync(command.ProjectId,
            command.OperationId,
            DevelopmentOperationPhases.Completed,
            async () =>
            {
                var task = await _dbContext.DevelopmentTasks.SingleOrDefaultAsync(entity => entity.Id == command.TaskId && entity.ProjectId == command.ProjectId, cancellationToken)
                                           .ConfigureAwait(false)
                           ?? throw new KeyNotFoundException($"Development task '{command.TaskId}' was not found.");
                if (command.AttemptId is { } attemptId && !await _dbContext.DevelopmentAttempts.AnyAsync(entity => entity.Id == attemptId && entity.TaskId == task.Id, cancellationToken)
                                                                           .ConfigureAwait(false))
                {
                    throw new KeyNotFoundException($"Development attempt '{attemptId}' was not found on the task.");
                }

                var artifact = BuildArtifact(command);
                _dbContext.DevelopmentArtifacts.Add(artifact);

                return await AddEventAsync(command.ProjectId,
                    command.TaskId,
                    command.AttemptId,
                    command.OperationId,
                    DevelopmentOperationPhases.Completed,
                    "ArtifactAttached",
                    "Attached",
                    command.Kind.ToString(),
                    version: 1,
                    command.ArtifactId,
                    detailJson: null,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> RecordWorkspaceSecretsAsync(Guid taskId,
        Guid attemptId,
        IReadOnlyList<string> repositoryRelativePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryRelativePaths);
        if (repositoryRelativePaths.Count == 0)
        {
            throw new ArgumentException("A workspace-secret event must name at least one path.", nameof(repositoryRelativePaths));
        }

        var projectId = await ProjectIdForTaskAsync(taskId, cancellationToken).ConfigureAwait(false);

        // Keyed on the ATTEMPT id rather than a fresh operation id, which is what makes it idempotent: a task's
        // workspace is prepared once by the coder and again by validation, and the same finding recorded twice would
        // read as two separate discoveries.
        return await ExecuteOperationAsync(projectId,
            attemptId,
            WorkspaceSecretsOperationPhase,
            async () =>
            {
                if (!await _dbContext.DevelopmentAttempts.AnyAsync(entity => entity.Id == attemptId && entity.TaskId == taskId, cancellationToken).ConfigureAwait(false))
                {
                    throw new KeyNotFoundException($"Development attempt '{attemptId}' was not found on the task.");
                }

                return await AddEventAsync(projectId,
                    taskId,
                    attemptId,
                    attemptId,
                    WorkspaceSecretsOperationPhase,
                    "WorkspaceSecretsDetected",
                    "Detected",
                    repositoryRelativePaths.Count.ToString(CultureInfo.InvariantCulture),
                    version: 1,
                    artifactId: null,
                    JsonSerializer.SerializeToUtf8Bytes(repositoryRelativePaths, JsonOptions),
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> RecordWorkflowPolicyAsync(Guid taskId,
        Guid operationId,
        string policyText,
        IReadOnlyList<DevelopmentWorkflowRuleSetReference> ruleSets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policyText);
        ArgumentNullException.ThrowIfNull(ruleSets);

        // Blank text is the CLEAR, and it is the whole event vocabulary this needs: the snapshot query answers off the
        // LATEST row, so a row saying "nothing applies" is exactly a row that revokes the one before it. A second event
        // type would have to be understood by that query, by the docs catalog and by every reader of the log to say the
        // same thing. What a clear must NOT do is name rule sets it is not applying.
        var cleared = string.IsNullOrWhiteSpace(policyText);
        if (cleared ? ruleSets.Count != 0 : ruleSets.Count == 0)
        {
            throw new ArgumentException("A workflow-policy event must name at least one rule set, and a cleared one must name none.",
                nameof(ruleSets));
        }

        var projectId = await ProjectIdForTaskAsync(taskId, cancellationToken).ConfigureAwait(false);

        // Keyed on the caller's own deterministic operation id, which is what makes it idempotent: a workflow re-binds
        // its node run to the same task after a crash, and the same policy recorded twice would read as two separate
        // injections and be replayed to the coder as the later one.
        return await ExecuteOperationAsync(projectId,
            operationId,
            WorkflowPolicyOperationPhase,
            async () => await AddEventAsync(projectId,
                taskId,
                attemptId: null,
                operationId,
                WorkflowPolicyOperationPhase,
                "WorkflowPolicyApplied",
                cleared ? "Cleared" : "Applied",
                ruleSets.Count.ToString(CultureInfo.InvariantCulture),
                version: 1,
                artifactId: null,
                JsonSerializer.SerializeToUtf8Bytes(new WorkflowPolicyDetail(policyText, ruleSets), JsonOptions),
                cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ReconcileRunningAttemptsAsync(string sanitizedReason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedReason);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var attempts = await _dbContext.DevelopmentAttempts.Where(entity => entity.Status == DevelopmentAttemptStatus.Running)
                                       .OrderBy(entity => entity.StartedAtUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);
        if (attempts.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        foreach (var attempt in attempts)
        {
            var task = await _dbContext.DevelopmentTasks.SingleAsync(entity => entity.Id == attempt.TaskId, cancellationToken).ConfigureAwait(false);
            var operationId = attempt.Id;
            if (await FindOperationCoreAsync(task.ProjectId, operationId, StartupOperationPhase, cancellationToken).ConfigureAwait(false) is not null)
            {
                continue;
            }

            attempt.Status = DevelopmentAttemptStatus.Interrupted;
            attempt.EndedAtUtc = Now();
            attempt.TerminalReason = sanitizedReason;
            attempt.Version++;
            await AddEventAsync(task.ProjectId,
                task.Id,
                attempt.Id,
                operationId,
                StartupOperationPhase,
                "AttemptInterrupted",
                "Interrupted",
                DevelopmentAttemptStatus.Interrupted.ToString(),
                attempt.Version,
                artifactId: null,
                detailJson: null,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return attempts.Count;
        }
        catch (DbUpdateConcurrencyException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw new DevelopmentConcurrencyException("Concurrent startup reconciliation won the optimistic concurrency race.", exception);
        }
    }

    public async Task<int> ReconcileIncompleteValidationsAsync(string sanitizedReason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedReason);
        var validations = await _dbContext.DevelopmentTasks.AsNoTracking()
                                          .Where(entity => entity.Status == DevelopmentTaskStatus.Validation)
                                          .Select(entity => new
                                          {
                                              entity.Id,
                                              entity.Version
                                          })
                                          .ToListAsync(cancellationToken)
                                          .ConfigureAwait(false);
        var reconciled = 0;
        foreach (var validation in validations)
        {
            try
            {
                _ = await InvalidateEvidenceAsync(new DevelopmentInvalidateEvidenceCommand(validation.Id,
                            validation.Id,
                            validation.Version,
                            sanitizedReason),
                        cancellationToken)
                    .ConfigureAwait(false);
                reconciled++;
            }
            catch (DevelopmentConcurrencyException)
            {
                // A live operation determined the authoritative task state while startup reconciliation was reading it.
            }
            catch (DevelopmentInvalidTransitionException)
            {
                // The task already left Validation; no recovery mutation remains.
            }
        }

        return reconciled;
    }

    public Task<DevelopmentOperationResult?> FindOperationAsync(Guid projectId, Guid operationId, string phase, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        return FindOperationCoreAsync(projectId, operationId, phase, cancellationToken);
    }

    public async Task<DevelopmentOperationResult> RecordApplyStartedAsync(Guid operationId,
        DevelopmentApprovedApplySubject subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return await ExecuteOperationAsync(subject.ProjectId,
            operationId,
            DevelopmentOperationPhases.ApplyStarted,
            async () =>
            {
                var task = await LoadApplyTaskAsync(subject, cancellationToken).ConfigureAwait(false);
                EnsureVersion(task.Version, subject.ExpectedTaskVersion, "task");
                if (task.Status != DevelopmentTaskStatus.AwaitingApply
                    || string.IsNullOrWhiteSpace(subject.SubjectHash)
                    || !string.Equals(task.ApprovedSubjectHash, subject.SubjectHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DevelopmentInvalidTransitionException("Only the exact independently approved subject can start host apply.");
                }

                var detail = Utf8(JsonSerializer.Serialize(subject, JsonOptions));
                return await AddEventAsync(subject.ProjectId,
                    subject.TaskId,
                    attemptId: null,
                    operationId,
                    DevelopmentOperationPhases.ApplyStarted,
                    "ApplyStarted",
                    "Started",
                    task.Status.ToString(),
                    task.Version,
                    artifactId: null,
                    detail,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> CompleteApplyAsync(Guid operationId,
        DevelopmentApprovedApplySubject subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return await ExecuteOperationAsync(subject.ProjectId,
            operationId,
            DevelopmentOperationPhases.ApplyCompleted,
            async () =>
            {
                var task = await LoadApplyTaskAsync(subject, cancellationToken).ConfigureAwait(false);
                EnsureVersion(task.Version, subject.ExpectedTaskVersion, "task");
                if (task.Status != DevelopmentTaskStatus.AwaitingApply)
                {
                    throw new DevelopmentInvalidTransitionException("Only a task awaiting explicit apply can complete.");
                }

                if (!string.Equals(task.ApprovedSubjectHash, subject.SubjectHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DevelopmentInvalidTransitionException("The completed apply subject no longer matches the independently approved subject.");
                }

                task.Status = DevelopmentTaskStatus.Completed;
                task.UpdatedAtUtc = Now();
                task.Version++;
                return await AddEventAsync(subject.ProjectId,
                    subject.TaskId,
                    attemptId: null,
                    operationId,
                    DevelopmentOperationPhases.ApplyCompleted,
                    "ApplyCompleted",
                    "Completed",
                    task.Status.ToString(),
                    task.Version,
                    artifactId: null,
                    detailJson: null,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> BlockApplyAsync(Guid operationId,
        DevelopmentApprovedApplySubject subject,
        string sanitizedReason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedReason);
        return await ExecuteOperationAsync(subject.ProjectId,
            operationId,
            DevelopmentOperationPhases.ApplyBlocked,
            async () =>
            {
                var task = await LoadApplyTaskAsync(subject, cancellationToken).ConfigureAwait(false);
                if (task.Status != DevelopmentTaskStatus.Blocked)
                {
                    task.Status = DevelopmentTaskStatus.Blocked;
                    task.BlockedReason = sanitizedReason;
                    task.BlockedAtUtc = Now();
                    task.UpdatedAtUtc = task.BlockedAtUtc.Value;
                    task.Version++;
                }

                return await AddEventAsync(subject.ProjectId,
                    subject.TaskId,
                    attemptId: null,
                    operationId,
                    DevelopmentOperationPhases.ApplyBlocked,
                    "ApplyBlocked",
                    "Blocked",
                    task.Status.ToString(),
                    task.Version,
                    artifactId: null,
                    Utf8(JsonSerializer.Serialize(new
                    {
                        reason = sanitizedReason
                    }, JsonOptions)),
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
