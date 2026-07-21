namespace XE_Local_AI_Engine.Tests.Development;

using XE_Local_AI_Engine.Tests.Testing;

public sealed class DevelopmentRestartRecoverySpikeTests
{
    [Test]
    public void PersistenceHarness_ContainsExactlyFivePrimaryConcepts()
    {
        var entityTypes = DevelopmentRestartRecoveryHarness.PersistentEntityTypes;

        AssertEx.Equal(expected: 5, entityTypes.Count);
        AssertEx.Contains(entityTypes, typeof(DevelopmentProject));
        AssertEx.Contains(entityTypes, typeof(DevelopmentTask));
        AssertEx.Contains(entityTypes, typeof(DevelopmentAttempt));
        AssertEx.Contains(entityTypes, typeof(DevelopmentArtifact));
        AssertEx.Contains(entityTypes, typeof(DevelopmentEvent));
    }

    [Test]
    [Arguments(DevelopmentInterruptionBoundary.BeforeFirstToken)]
    [Arguments(DevelopmentInterruptionBoundary.MidStream)]
    [Arguments(DevelopmentInterruptionBoundary.DuringReadTool)]
    public async Task RecoverAsync_WhenInterruptedBeforeMutation_CreatesReplacementWithoutReplay(DevelopmentInterruptionBoundary boundary)
    {
        await using var harness = await DevelopmentRestartRecoveryHarness.CreateAsync().ConfigureAwait(false);
        var interrupted = await harness.StartAndInterruptAsync(boundary).ConfigureAwait(false);
        var beforeRecovery = await harness.CaptureWorkspaceAsync().ConfigureAwait(false);
        var originalArtifactIds = harness.Artifacts.Select(artifact => artifact.Id).ToArray();
        var readExecutions = harness.ReadToolExecutions;

        var recovery = await harness.RecoverAsync().ConfigureAwait(false);
        var replacement = await harness.CreateReplacementAttemptAsync(interrupted.Id).ConfigureAwait(false);
        var afterReplacement = await harness.CaptureWorkspaceAsync().ConfigureAwait(false);

        AssertEx.Equal(DevelopmentAttemptStatus.Interrupted, interrupted.Status);
        AssertEx.Equal(interrupted.Id, replacement.PredecessorAttemptId);
        AssertEx.Equal(DevelopmentAttemptStatus.Running, replacement.Status);
        AssertEx.Equal(expected: 1, recovery.InterruptedAttempts);
        AssertEx.True(recovery.ReplacementAllowed);
        AssertEx.Equal(beforeRecovery.SubjectHash, afterReplacement.SubjectHash);
        AssertEx.Equal(beforeRecovery.ManifestHash, afterReplacement.ManifestHash);
        AssertEx.True(originalArtifactIds.All(id => harness.Artifacts.Any(artifact => artifact.Id == id)), "Recovery must preserve existing artifacts.");
        AssertEx.Equal(readExecutions, harness.ReadToolExecutions);
        AssertEx.Equal(expected: 0, harness.WriteCommandExecutions);
        AssertEx.Equal(expected: 0, harness.ValidationCommandExecutions);
        AssertEx.Equal(harness.ProtectedBranchCommit, await harness.ReadProtectedBranchCommitAsync().ConfigureAwait(false));
    }

    [Test]
    public async Task RecoverAsync_WhenInterruptedAfterWorkspaceWrite_PreservesDiffAndDoesNotReplayCommand()
    {
        await using var harness = await DevelopmentRestartRecoveryHarness.CreateAsync().ConfigureAwait(false);
        var interrupted = await harness.StartAndInterruptAsync(DevelopmentInterruptionBoundary.AfterWorkspaceWriteBeforeToolResult).ConfigureAwait(false);
        var interruptedSubject = await harness.CaptureWorkspaceAsync().ConfigureAwait(false);

        var recovery = await harness.RecoverAsync().ConfigureAwait(false);
        var replacement = await harness.CreateReplacementAttemptAsync(interrupted.Id).ConfigureAwait(false);
        var replacementSubject = await harness.CaptureWorkspaceAsync().ConfigureAwait(false);

        AssertEx.Equal(expected: 1, harness.WriteCommandExecutions);
        AssertEx.True(interruptedSubject.ChangedFiles.Contains("tracked.txt"), "The interrupted write must remain visible in the Git worktree.");
        AssertEx.Equal(interruptedSubject.SubjectHash, replacementSubject.SubjectHash);
        AssertEx.Equal(interruptedSubject.ManifestHash, replacementSubject.ManifestHash);
        AssertEx.Equal(interrupted.Id, replacement.PredecessorAttemptId);
        AssertEx.True(recovery.ReplacementAllowed);
        AssertEx.False(harness.Artifacts.Any(artifact => artifact.Kind == DevelopmentArtifactKind.CommandResult),
            "A crash before tool-result persistence must not fabricate command evidence.");
        AssertEx.Equal(harness.ProtectedBranchCommit, await harness.ReadProtectedBranchCommitAsync().ConfigureAwait(false));
    }

    [Test]
    public async Task RecoverAsync_WhenInterruptedAfterValidationArtifact_PreservesEvidenceWithoutRerunningValidation()
    {
        await using var harness = await DevelopmentRestartRecoveryHarness.CreateAsync().ConfigureAwait(false);
        var interrupted = await harness.StartAndInterruptAsync(DevelopmentInterruptionBoundary.AfterValidationArtifactBeforeTerminalization).ConfigureAwait(false);
        var validation = harness.Artifacts.Single(artifact => artifact.Kind == DevelopmentArtifactKind.ValidationReport);

        var recovery = await harness.RecoverAsync().ConfigureAwait(false);
        var replacement = await harness.CreateReplacementAttemptAsync(interrupted.Id).ConfigureAwait(false);

        AssertEx.True(validation.IsValid);
        AssertEx.Equal(expected: 1, harness.ValidationCommandExecutions);
        AssertEx.Equal(expected: 0, recovery.InvalidatedArtifacts);
        AssertEx.Equal(interrupted.Id, replacement.PredecessorAttemptId);
        AssertEx.ContainsSingle(harness.Events, item => item.EventType == "AttemptInterrupted");
        AssertEx.ContainsSingle(harness.Events, item => item.EventType == "ReplacementAttemptStarted");
    }

    [Test]
    public async Task RecoverAsync_WhenWorkspaceSubjectChanges_InvalidatesValidationAndReviewEvidence()
    {
        await using var harness = await DevelopmentRestartRecoveryHarness.CreateAsync().ConfigureAwait(false);
        var interrupted = await harness.StartAndInterruptAsync(DevelopmentInterruptionBoundary.AfterValidationArtifactBeforeTerminalization).ConfigureAwait(false);
        await harness.AttachReviewEvidenceAsync(interrupted.Id).ConfigureAwait(false);
        await harness.MutateWorkspaceOutsideCoordinatorAsync("base\noperator mutation\n").ConfigureAwait(false);

        var recovery = await harness.RecoverAsync().ConfigureAwait(false);
        var replacement = await harness.CreateReplacementAttemptAsync(interrupted.Id).ConfigureAwait(false);

        var gateEvidence = harness.Artifacts.Where(artifact => artifact.Kind is DevelopmentArtifactKind.ValidationReport or DevelopmentArtifactKind.ReviewReport).ToArray();
        AssertEx.Equal(expected: 2, gateEvidence.Length);
        AssertEx.True(gateEvidence.All(artifact => !artifact.IsValid), "Subject/manifest mismatches must invalidate every stale gate artifact.");
        AssertEx.Equal(expected: 2, recovery.InvalidatedArtifacts);
        AssertEx.True(recovery.ReplacementAllowed);
        AssertEx.Equal(interrupted.Id, replacement.PredecessorAttemptId);
        AssertEx.Equal(expected: 2, harness.Events.Count(item => item.EventType == "EvidenceInvalidated"));
        AssertEx.Equal(expected: 1, harness.ValidationCommandExecutions);
    }

    [Test]
    public async Task RecoverAsync_WhenBaseCommitCannotBeReconciled_BlocksReplacement()
    {
        await using var harness = await DevelopmentRestartRecoveryHarness.CreateAsync().ConfigureAwait(false);
        var interrupted = await harness.StartAndInterruptAsync(DevelopmentInterruptionBoundary.AfterValidationArtifactBeforeTerminalization).ConfigureAwait(false);
        await harness.CommitWorkspaceMutationOutsideCoordinatorAsync("unexpected committed mutation\n").ConfigureAwait(false);

        var recovery = await harness.RecoverAsync().ConfigureAwait(false);
        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(
            () => harness.CreateReplacementAttemptAsync(interrupted.Id)).ConfigureAwait(false);

        AssertEx.False(recovery.ReplacementAllowed);
        AssertEx.True(harness.Task.IsBlocked);
        AssertEx.Contains(exception.Message, "cannot be reconciled", StringComparison.OrdinalIgnoreCase);
        AssertEx.ContainsSingle(harness.Events, item => item.EventType == "RecoveryBlockedUnreconciledBase");
        AssertEx.False(harness.Artifacts.Single(artifact => artifact.Kind == DevelopmentArtifactKind.ValidationReport).IsValid);
        AssertEx.Equal(expected: 1, harness.ValidationCommandExecutions);
        AssertEx.Equal(harness.ProtectedBranchCommit, await harness.ReadProtectedBranchCommitAsync().ConfigureAwait(false));
    }

    [Test]
    public async Task RecoverAsync_WhenRepeated_IsIdempotentAndLeavesTerminalStatusesUnchanged()
    {
        await using var harness = await DevelopmentRestartRecoveryHarness.CreateAsync().ConfigureAwait(false);
        var running = await harness.StartAndInterruptAsync(DevelopmentInterruptionBoundary.BeforeFirstToken).ConfigureAwait(false);
        var artifactCount = harness.Artifacts.Count;
        var pending = harness.SeedAttempt(DevelopmentAttemptStatus.Pending);
        var succeeded = harness.SeedAttempt(DevelopmentAttemptStatus.Succeeded);
        var failed = harness.SeedAttempt(DevelopmentAttemptStatus.Failed);
        var interrupted = harness.SeedAttempt(DevelopmentAttemptStatus.Interrupted);
        var cancelled = harness.SeedAttempt(DevelopmentAttemptStatus.Cancelled);

        var first = await harness.RecoverAsync().ConfigureAwait(false);
        var second = await harness.RecoverAsync().ConfigureAwait(false);

        AssertEx.Equal(expected: 1, first.InterruptedAttempts);
        AssertEx.Equal(expected: 0, second.InterruptedAttempts);
        AssertEx.Equal(DevelopmentAttemptStatus.Interrupted, running.Status);
        AssertEx.Equal(DevelopmentAttemptStatus.Pending, pending.Status);
        AssertEx.Equal(DevelopmentAttemptStatus.Succeeded, succeeded.Status);
        AssertEx.Equal(DevelopmentAttemptStatus.Failed, failed.Status);
        AssertEx.Equal(DevelopmentAttemptStatus.Interrupted, interrupted.Status);
        AssertEx.Equal(DevelopmentAttemptStatus.Cancelled, cancelled.Status);
        AssertEx.ContainsSingle(harness.Events, item => item.AttemptId == running.Id && item.EventType == "AttemptInterrupted");
        AssertEx.Equal(artifactCount, harness.Artifacts.Count);
    }

    [Test]
    public async Task RecoverAsync_WhenConcurrent_InterruptsAttemptExactlyOnce()
    {
        await using var harness = await DevelopmentRestartRecoveryHarness.CreateAsync().ConfigureAwait(false);
        var running = await harness.StartAndInterruptAsync(DevelopmentInterruptionBoundary.DuringReadTool).ConfigureAwait(false);
        var artifactCount = harness.Artifacts.Count;

        var recoveries = await Task.WhenAll(harness.RecoverAsync(), harness.RecoverAsync()).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, recoveries.Sum(recovery => recovery.InterruptedAttempts));
        AssertEx.Equal(DevelopmentAttemptStatus.Interrupted, running.Status);
        AssertEx.ContainsSingle(harness.Events, item => item.AttemptId == running.Id && item.EventType == "AttemptInterrupted");
        AssertEx.Equal(artifactCount, harness.Artifacts.Count);
        AssertEx.Equal(expected: 1, harness.ReadToolExecutions);
    }
}
