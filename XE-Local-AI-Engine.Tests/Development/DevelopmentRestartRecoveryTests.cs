namespace XE_Local_AI_Engine.Tests.Development;

using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class DevelopmentRestartRecoveryTests
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
        await using var harness = await DevelopmentRestartRecoveryHarness.CreateAsync();
        var interrupted = await harness.StartAndInterruptAsync(boundary);
        var beforeRecovery = await harness.CaptureWorkspaceAsync();
        var originalArtifactIds = harness.Artifacts.Select(artifact => artifact.Id).ToArray();
        var readExecutions = harness.ReadToolExecutions;

        var recovery = await harness.RecoverAsync();
        var replacement = await harness.CreateReplacementAttemptAsync(interrupted.Id);
        var afterReplacement = await harness.CaptureWorkspaceAsync();

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
        AssertEx.Equal(harness.ProtectedBranchCommit, await harness.ReadProtectedBranchCommitAsync());
    }

    [Test]
    public async Task RecoverAsync_WhenInterruptedAfterWorkspaceWrite_PreservesDiffAndDoesNotReplayCommand()
    {
        await using var harness = await DevelopmentRestartRecoveryHarness.CreateAsync();
        var interrupted = await harness.StartAndInterruptAsync(DevelopmentInterruptionBoundary.AfterWorkspaceWriteBeforeToolResult);
        var interruptedSubject = await harness.CaptureWorkspaceAsync();

        var recovery = await harness.RecoverAsync();
        var replacement = await harness.CreateReplacementAttemptAsync(interrupted.Id);
        var replacementSubject = await harness.CaptureWorkspaceAsync();

        AssertEx.Equal(expected: 1, harness.WriteCommandExecutions);
        AssertEx.True(interruptedSubject.ChangedFiles.Contains("tracked.txt"), "The interrupted write must remain visible in the Git worktree.");
        AssertEx.Equal(interruptedSubject.SubjectHash, replacementSubject.SubjectHash);
        AssertEx.Equal(interruptedSubject.ManifestHash, replacementSubject.ManifestHash);
        AssertEx.Equal(interrupted.Id, replacement.PredecessorAttemptId);
        AssertEx.True(recovery.ReplacementAllowed);
        AssertEx.False(harness.Artifacts.Any(artifact => artifact.Kind == DevelopmentArtifactKind.CommandResult),
            "A crash before tool-result persistence must not fabricate command evidence.");
        AssertEx.Equal(harness.ProtectedBranchCommit, await harness.ReadProtectedBranchCommitAsync());
    }

    [Test]
    public async Task RecoverAsync_WhenInterruptedAfterValidationArtifact_PreservesEvidenceWithoutRerunningValidation()
    {
        await using var harness = await DevelopmentRestartRecoveryHarness.CreateAsync();
        var interrupted = await harness.StartAndInterruptAsync(DevelopmentInterruptionBoundary.AfterValidationArtifactBeforeTerminalization);
        var validation = harness.Artifacts.Single(artifact => artifact.Kind == DevelopmentArtifactKind.ValidationReport);

        var recovery = await harness.RecoverAsync();
        var replacement = await harness.CreateReplacementAttemptAsync(interrupted.Id);

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
        await using var harness = await DevelopmentRestartRecoveryHarness.CreateAsync();
        var interrupted = await harness.StartAndInterruptAsync(DevelopmentInterruptionBoundary.AfterValidationArtifactBeforeTerminalization);
        await harness.AttachReviewEvidenceAsync(interrupted.Id);
        await harness.MutateWorkspaceOutsideCoordinatorAsync("base\noperator mutation\n");

        var recovery = await harness.RecoverAsync();
        var replacement = await harness.CreateReplacementAttemptAsync(interrupted.Id);

        var approvalEvidence = harness.Artifacts.Where(artifact => artifact.Kind is DevelopmentArtifactKind.ValidationReport or DevelopmentArtifactKind.ReviewReport).ToArray();
        AssertEx.Equal(expected: 2, approvalEvidence.Length);
        AssertEx.True(approvalEvidence.All(artifact => !artifact.IsValid), "Subject/manifest mismatches must invalidate every stale approval artifact.");
        AssertEx.Equal(expected: 2, recovery.InvalidatedArtifacts);
        AssertEx.True(recovery.ReplacementAllowed);
        AssertEx.Equal(interrupted.Id, replacement.PredecessorAttemptId);
        AssertEx.Equal(expected: 2, harness.Events.Count(item => item.EventType == "EvidenceInvalidated"));
        AssertEx.Equal(expected: 1, harness.ValidationCommandExecutions);
    }

    [Test]
    public async Task RecoverAsync_WhenBaseCommitCannotBeReconciled_BlocksReplacement()
    {
        await using var harness = await DevelopmentRestartRecoveryHarness.CreateAsync();
        var interrupted = await harness.StartAndInterruptAsync(DevelopmentInterruptionBoundary.AfterValidationArtifactBeforeTerminalization);
        await harness.CommitWorkspaceMutationOutsideCoordinatorAsync("unexpected committed mutation\n");

        var recovery = await harness.RecoverAsync();
        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() => harness.CreateReplacementAttemptAsync(interrupted.Id));

        AssertEx.False(recovery.ReplacementAllowed);
        AssertEx.True(harness.Task.IsBlocked);
        AssertEx.Contains(exception.Message, "cannot be reconciled", StringComparison.OrdinalIgnoreCase);
        AssertEx.ContainsSingle(harness.Events, item => item.EventType == "RecoveryBlockedUnreconciledBase");
        AssertEx.False(harness.Artifacts.Single(artifact => artifact.Kind == DevelopmentArtifactKind.ValidationReport).IsValid);
        AssertEx.Equal(expected: 1, harness.ValidationCommandExecutions);
        AssertEx.Equal(harness.ProtectedBranchCommit, await harness.ReadProtectedBranchCommitAsync());
    }

    [Test]
    public async Task RecoverAsync_WhenRepeated_IsIdempotentAndLeavesTerminalStatusesUnchanged()
    {
        await using var harness = await DevelopmentRestartRecoveryHarness.CreateAsync();
        var running = await harness.StartAndInterruptAsync(DevelopmentInterruptionBoundary.BeforeFirstToken);
        var artifactCount = harness.Artifacts.Count;
        var pending = harness.SeedAttempt(DevelopmentAttemptStatus.Pending);
        var succeeded = harness.SeedAttempt(DevelopmentAttemptStatus.Succeeded);
        var failed = harness.SeedAttempt(DevelopmentAttemptStatus.Failed);
        var interrupted = harness.SeedAttempt(DevelopmentAttemptStatus.Interrupted);
        var cancelled = harness.SeedAttempt(DevelopmentAttemptStatus.Cancelled);

        var first = await harness.RecoverAsync();
        var second = await harness.RecoverAsync();

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
        await using var harness = await DevelopmentRestartRecoveryHarness.CreateAsync();
        var running = await harness.StartAndInterruptAsync(DevelopmentInterruptionBoundary.DuringReadTool);
        var artifactCount = harness.Artifacts.Count;

        var recoveries = await Task.WhenAll(harness.RecoverAsync(), harness.RecoverAsync());

        AssertEx.Equal(expected: 1, recoveries.Sum(recovery => recovery.InterruptedAttempts));
        AssertEx.Equal(DevelopmentAttemptStatus.Interrupted, running.Status);
        AssertEx.ContainsSingle(harness.Events, item => item.AttemptId == running.Id && item.EventType == "AttemptInterrupted");
        AssertEx.Equal(artifactCount, harness.Artifacts.Count);
        AssertEx.Equal(expected: 1, harness.ReadToolExecutions);
    }
}
