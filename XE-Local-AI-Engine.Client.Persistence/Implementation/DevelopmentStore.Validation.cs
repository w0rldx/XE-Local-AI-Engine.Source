namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class DevelopmentStore
{
    public async Task<DevelopmentOperationResult> StartValidationAsync(DevelopmentStartValidationCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var projectId = await ProjectIdForTaskAsync(command.TaskId, cancellationToken).ConfigureAwait(false);

        return await ExecuteOperationAsync(projectId,
            command.OperationId,
            DevelopmentOperationPhases.Completed,
            async () =>
            {
                var task = await _dbContext.DevelopmentTasks.SingleAsync(entity => entity.Id == command.TaskId, cancellationToken).ConfigureAwait(false);
                EnsureVersion(task.Version, command.ExpectedTaskVersion, "task");
                if (task.Status != DevelopmentTaskStatus.InProgress)
                {
                    throw new DevelopmentInvalidTransitionException("Deterministic validation requires an in-progress Development task.");
                }

                if (await _dbContext.DevelopmentAttempts.AnyAsync(entity => entity.TaskId == task.Id
                                                                            && (entity.Status == DevelopmentAttemptStatus.Pending
                                                                                || entity.Status == DevelopmentAttemptStatus.Running),
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new DevelopmentInvalidTransitionException("Deterministic validation cannot overlap an active Development attempt.");
                }

                var latestAttempt = await _dbContext.DevelopmentAttempts
                                                    .Where(entity => entity.TaskId == task.Id)
                                                    .OrderByDescending(entity => entity.StartedAtUtc)
                                                    .ThenByDescending(entity => entity.Id)
                                                    .FirstOrDefaultAsync(cancellationToken)
                                                    .ConfigureAwait(false);
                if (latestAttempt is null
                    || latestAttempt.Role != DevelopmentAttemptRole.Coder
                    || latestAttempt.Status != DevelopmentAttemptStatus.Succeeded)
                {
                    throw new DevelopmentInvalidTransitionException("Deterministic validation requires the latest Development attempt to be a successful coder attempt.");
                }

                task.Status = DevelopmentTaskStatus.Validation;
                task.UpdatedAtUtc = Now();
                task.Version++;

                return await AddEventAsync(projectId,
                    task.Id,
                    latestAttempt.Id,
                    command.OperationId,
                    DevelopmentOperationPhases.Completed,
                    "ValidationStarted",
                    "Started",
                    task.Status.ToString(),
                    task.Version,
                    artifactId: null,
                    detailJson: null,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> InvalidateEvidenceAsync(DevelopmentInvalidateEvidenceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.SanitizedReason, "sanitizedReason");
        var projectId = await ProjectIdForTaskAsync(command.TaskId, cancellationToken).ConfigureAwait(false);

        return await ExecuteOperationAsync(projectId,
            command.OperationId,
            DevelopmentOperationPhases.Completed,
            async () =>
            {
                var task = await _dbContext.DevelopmentTasks.SingleAsync(entity => entity.Id == command.TaskId, cancellationToken).ConfigureAwait(false);
                EnsureVersion(task.Version, command.ExpectedTaskVersion, "task");
                if (task.Status is not (DevelopmentTaskStatus.Validation
                    or DevelopmentTaskStatus.InReview
                    or DevelopmentTaskStatus.AwaitingApply))
                {
                    throw new DevelopmentInvalidTransitionException("Only validation, review, or approved evidence can be invalidated.");
                }

                task.Status = DevelopmentTaskStatus.InProgress;
                task.UpdatedAtUtc = Now();
                task.ApprovedSubjectHash = null;
                task.BlockedReason = null;
                task.BlockedAtUtc = null;
                task.Version++;
                await _dbContext.DevelopmentArtifacts
                                .Where(entity => entity.TaskId == task.Id
                                                 && entity.IsValid
                                                 && (entity.Kind == DevelopmentArtifactKind.ValidationReport
                                                     || entity.Kind == DevelopmentArtifactKind.ReviewReport))
                                .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.IsValid, false), cancellationToken)
                                .ConfigureAwait(false);

                return await AddEventAsync(projectId,
                    task.Id,
                    attemptId: null,
                    command.OperationId,
                    DevelopmentOperationPhases.Completed,
                    "EvidenceInvalidated",
                    "Invalidated",
                    task.Status.ToString(),
                    task.Version,
                    artifactId: null,
                    detailJson: Utf8(JsonSerializer.Serialize(new
                    {
                        reason = command.SanitizedReason
                    }, JsonOptions)),
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> FinalizeValidationAsync(DevelopmentFinalizeValidationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateArtifactCommand(command.Artifact);
        if (command.Artifact.Kind != DevelopmentArtifactKind.ValidationReport
            || command.TargetStatus is not (DevelopmentTaskStatus.InReview or DevelopmentTaskStatus.ChangesRequested))
        {
            throw new ArgumentException("Validation finalization requires a validation artifact and a review or rework target.", nameof(command));
        }

        return await ExecuteOperationAsync(command.Artifact.ProjectId,
            command.OperationId,
            DevelopmentOperationPhases.Completed,
            async () =>
            {
                var task = await _dbContext.DevelopmentTasks.SingleOrDefaultAsync(entity => entity.Id == command.Artifact.TaskId
                                                                                            && entity.ProjectId == command.Artifact.ProjectId,
                                               cancellationToken)
                                           .ConfigureAwait(false)
                           ?? throw new KeyNotFoundException($"Development task '{command.Artifact.TaskId}' was not found.");
                EnsureVersion(task.Version, command.ExpectedTaskVersion, "task");
                if (task.Status != DevelopmentTaskStatus.Validation)
                {
                    throw new DevelopmentInvalidTransitionException("Only an active deterministic validation can be finalized.");
                }

                EnsureLegalTransition(task.Status, command.TargetStatus);

                var attemptId = command.Artifact.AttemptId
                                ?? throw new DevelopmentInvalidTransitionException("A validation artifact must identify its coder attempt.");
                var attempt = await _dbContext.DevelopmentAttempts.SingleOrDefaultAsync(entity => entity.Id == attemptId && entity.TaskId == task.Id,
                                                  cancellationToken)
                                              .ConfigureAwait(false)
                              ?? throw new DevelopmentInvalidTransitionException("The validation artifact coder attempt was not found on the task.");
                if (attempt.Role != DevelopmentAttemptRole.Coder || attempt.Status != DevelopmentAttemptStatus.Succeeded)
                {
                    throw new DevelopmentInvalidTransitionException("A validation artifact must originate from a successful coder attempt.");
                }

                var artifact = BuildArtifact(command.Artifact);
                _dbContext.DevelopmentArtifacts.Add(artifact);
                var now = Now();
                task.Status = command.TargetStatus;
                task.UpdatedAtUtc = now;
                task.Version++;
                task.ApprovedSubjectHash = null;
                if (command.TargetStatus == DevelopmentTaskStatus.InReview)
                {
                    if (task.CurrentReviewRound >= task.MaxReviewRounds)
                    {
                        throw new DevelopmentInvalidTransitionException("The configured maximum review rounds has been reached.");
                    }

                    task.CurrentReviewRound++;

                    // A gate that PASSES clears the last failure's sentence. Nothing else on the recovery path does:
                    // not the coder round StartAttemptAsync starts, not FinalizeReviewAsync, not CompleteApplyAsync —
                    // so without this an approved, applied task kept rendering "Deterministic validation failed" under
                    // a green badge in the Development overview, which reads it with no status gate.
                    task.BlockedReason = null;
                }
                else
                {
                    // A failed gate SPENDS a round, exactly as a reviewer's rejection does. It has to: the round budget
                    // is the only bound on this loop, and a rejection that cost nothing let a task whose deterministic
                    // validation fails deterministically ask for coder rounds forever. Charged here rather than on the
                    // hop into review because this hop never enters review, and the count is what StartNextActionAsync
                    // reads to stand a task down once the budget is gone — the same stand-down a task gets when the
                    // FINAL reviewer round rejects it, reached by the same route and with the same reason.
                    //
                    // Bounded rather than unconditional so the count can never exceed the maximum. Reaching the cap is
                    // unreachable on the live path (StartNextActionAsync blocks a task at the cap BEFORE it schedules
                    // validation), and this store method is callable on its own, so the branch answers what a caller at
                    // the cap should get: the task still lands at ChangesRequested carrying the reason, and the block
                    // arrives one coder round later off the count that is already at its limit.
                    if (task.CurrentReviewRound < task.MaxReviewRounds)
                    {
                        task.CurrentReviewRound++;
                    }

                    // The reason reaches the next coder round through the event log, as a reviewer's does. This column
                    // is the OPERATOR-facing copy — the same widening TransitionTaskAsync makes for its own rework
                    // target. It is overwritten by the next failure and cleared by the next PASS, above.
                    task.BlockedReason = command.SanitizedReason;
                    artifact.IsValid = false;
                    await _dbContext.DevelopmentArtifacts
                                    .Where(entity => entity.TaskId == task.Id
                                                     && entity.IsValid
                                                     && (entity.Kind == DevelopmentArtifactKind.ValidationReport
                                                         || entity.Kind == DevelopmentArtifactKind.ReviewReport))
                                    .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.IsValid, false), cancellationToken)
                                    .ConfigureAwait(false);
                }

                return await AddEventAsync(command.Artifact.ProjectId,
                    task.Id,
                    attempt.Id,
                    command.OperationId,
                    DevelopmentOperationPhases.Completed,
                    "ValidationFinalized",
                    command.TargetStatus == DevelopmentTaskStatus.InReview ? "Passed" : "Failed",
                    task.Status.ToString(),
                    task.Version,
                    artifact.Id,
                    detailJson: command.SanitizedReason is null
                        ? null
                        : Utf8(JsonSerializer.Serialize(new
                        {
                            reason = command.SanitizedReason
                        }, JsonOptions)),
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentOperationResult> FinalizeReviewAsync(DevelopmentFinalizeReviewCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateArtifactCommand(command.Artifact);
        if (command.Artifact.Kind != DevelopmentArtifactKind.ReviewReport
            || command.TargetStatus is not (DevelopmentTaskStatus.AwaitingApply or DevelopmentTaskStatus.ChangesRequested))
        {
            throw new ArgumentException("Review finalization requires a review artifact and an apply or rework target.", nameof(command));
        }

        return await ExecuteOperationAsync(command.Artifact.ProjectId,
            command.OperationId,
            DevelopmentOperationPhases.Completed,
            async () =>
            {
                var task = await _dbContext.DevelopmentTasks.SingleOrDefaultAsync(entity => entity.Id == command.Artifact.TaskId
                                                                                            && entity.ProjectId == command.Artifact.ProjectId,
                                               cancellationToken)
                                           .ConfigureAwait(false)
                           ?? throw new KeyNotFoundException($"Development task '{command.Artifact.TaskId}' was not found.");
                EnsureVersion(task.Version, command.ExpectedTaskVersion, "task");
                if (task.Status != DevelopmentTaskStatus.InReview)
                {
                    throw new DevelopmentInvalidTransitionException("Only an active independent review can be finalized.");
                }

                var attemptId = command.Artifact.AttemptId
                                ?? throw new DevelopmentInvalidTransitionException("A review artifact must identify its reviewer attempt.");
                var attempt = await _dbContext.DevelopmentAttempts.SingleOrDefaultAsync(entity => entity.Id == attemptId && entity.TaskId == task.Id,
                                                  cancellationToken)
                                              .ConfigureAwait(false)
                              ?? throw new DevelopmentInvalidTransitionException("The review artifact reviewer attempt was not found on the task.");
                EnsureVersion(attempt.Version, command.ExpectedAttemptVersion, "attempt");
                if (attempt.Role != DevelopmentAttemptRole.Reviewer || attempt.Status != DevelopmentAttemptStatus.Running)
                {
                    throw new DevelopmentInvalidTransitionException("A review artifact must originate from the running independent reviewer attempt.");
                }

                var artifact = BuildArtifact(command.Artifact);
                _dbContext.DevelopmentArtifacts.Add(artifact);
                var now = Now();
                attempt.Status = DevelopmentAttemptStatus.Succeeded;
                attempt.EndedAtUtc = now;
                attempt.TerminalReason = null;
                attempt.InputTokens = command.InputTokens;
                attempt.OutputTokens = command.OutputTokens;
                attempt.Version++;
                task.Status = command.TargetStatus;
                task.UpdatedAtUtc = now;
                task.Version++;
                task.ApprovedSubjectHash = command.TargetStatus == DevelopmentTaskStatus.AwaitingApply
                    ? command.ApprovedSubjectHash
                    : null;

                return await AddEventAsync(command.Artifact.ProjectId,
                    task.Id,
                    attempt.Id,
                    command.OperationId,
                    DevelopmentOperationPhases.Completed,
                    "ReviewFinalized",
                    command.TargetStatus == DevelopmentTaskStatus.AwaitingApply ? "Approved" : "ChangesRequested",
                    task.Status.ToString(),
                    task.Version,
                    artifact.Id,
                    detailJson: command.SanitizedReason is null
                        ? null
                        : Utf8(JsonSerializer.Serialize(new
                        {
                            reason = command.SanitizedReason
                        }, JsonOptions)),
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
