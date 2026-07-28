namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed record DevelopmentPatchPreview(
    DevelopmentApprovedApplySubject Subject,
    string Patch,
    IReadOnlyList<DevelopmentChangedFile> ChangedFiles);

internal interface IDevelopmentApplyService
{
    Task<DevelopmentPatchPreview> PreviewAsync(Guid taskId,
        DevelopmentRepositoryBinding repository,
        CancellationToken cancellationToken = default);

    Task<DevelopmentOperationResult> ApplyAsync(Guid taskId,
        Guid operationId,
        DevelopmentRepositoryBinding repository,
        CancellationToken cancellationToken = default);
}

internal sealed class DevelopmentApplyService(
    IDevelopmentStore store,
    IDevelopmentCoordinator coordinator,
    IDevelopmentWorkspaceProvider workspaceProvider,
    IDevelopmentEvidenceService evidence,
    IDevelopmentRepositoryBindingService repositoryBindings,
    TimeProvider timeProvider) : IDevelopmentApplyService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDevelopmentCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    private readonly IDevelopmentEvidenceService _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
    private readonly IDevelopmentRepositoryBindingService _repositoryBindings = repositoryBindings ?? throw new ArgumentNullException(nameof(repositoryBindings));
    private readonly IDevelopmentStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly IDevelopmentWorkspaceProvider _workspaceProvider = workspaceProvider ?? throw new ArgumentNullException(nameof(workspaceProvider));

    public async Task<DevelopmentPatchPreview> PreviewAsync(Guid taskId,
        DevelopmentRepositoryBinding repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var task = await _store.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task.Status != DevelopmentTaskStatus.AwaitingApply || string.IsNullOrWhiteSpace(task.ApprovedSubjectHash))
        {
            throw new DevelopmentInvalidTransitionException("Patch preview requires an independently approved task awaiting explicit apply.");
        }

        var project = await _store.GetProjectAsync(task.ProjectId, cancellationToken).ConfigureAwait(false);
        DevelopmentTrustPolicy.EnsureCurrent(project, _timeProvider);
        var attempts = await _store.ListAttemptsAsync(taskId, cancellationToken).ConfigureAwait(false);
        var reviewerAttempt = attempts.LastOrDefault(attempt => attempt.Role == DevelopmentAttemptRole.Reviewer
                                                                && attempt.Status == DevelopmentAttemptStatus.Succeeded)
                              ?? throw new DevelopmentInvalidTransitionException("Patch preview requires a successful independent reviewer attempt.");

        // The profile to judge this evidence by is the one the CODER attempt ran under, not the project's current
        // value. They are the same until a profile-edit path exists, and they diverge the moment one does: a project
        // edited after this patch was produced would otherwise make a historical attempt fail to apply because the
        // digests describe an edit rather than a defect. Selected the same way every other gate selects it, so all of
        // them agree on which attempt is authoritative.
        var coderAttempt = attempts.LastOrDefault(attempt => attempt.Role == DevelopmentAttemptRole.Coder
                                                             && attempt.Status == DevelopmentAttemptStatus.Succeeded)
                           ?? throw new DevelopmentInvalidTransitionException("Patch preview requires a successful coder attempt.");
        var coderSnapshot = await _store.GetExecutionSnapshotAsync(coderAttempt.Id, cancellationToken).ConfigureAwait(false);
        var snapshot = await _store.GetExecutionSnapshotAsync(reviewerAttempt.Id, cancellationToken).ConfigureAwait(false);
        var session = await _workspaceProvider.PrepareAsync(snapshot, repository, cancellationToken).ConfigureAwait(false);
        DevelopmentEvidenceSet current;
        try
        {
            current = await _evidence.ResolveCurrentAsync(taskId, session, cancellationToken).ConfigureAwait(false);
            var expectedProfileDigest = DevelopmentCommandProfileCatalog.ResolveStored(coderSnapshot.CommandProfileJson).ComputeDigest();
            var (validationArtifact, _) = await ReadValidationAsync(taskId, current, expectedProfileDigest, cancellationToken).ConfigureAwait(false);
            var (reviewArtifact, reviewReport) = await ReadReviewAsync(taskId, current, validationArtifact.Id, expectedProfileDigest, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(task.ApprovedSubjectHash, current.Current.SubjectHash, StringComparison.OrdinalIgnoreCase)
                || reviewReport.Disposition != DevelopmentReviewDisposition.Approved
                || reviewArtifact.AttemptId != reviewerAttempt.Id
                || reviewReport.ReviewRound != task.CurrentReviewRound)
            {
                throw new DevelopmentInvalidTransitionException("The current workspace subject is not the exact independently approved subject and review round.");
            }
        }
        catch (DevelopmentInvalidTransitionException)
        {
            await _evidence.InvalidateApprovalEvidenceAsync(taskId,
                "The exact Development apply evidence is no longer authoritative.",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var subject = new DevelopmentApprovedApplySubject(project.Id,
            task.Id,
            task.Version,
            current.Current.BaseCommit,
            current.Current.PatchHash,
            current.Current.ManifestHash,
            current.Current.ExpectedResultHash,
            current.PatchArtifact.ManagedReference
            ?? throw new DevelopmentInvalidTransitionException("The approved patch has no managed artifact reference."),
            current.ManifestArtifact.ManagedReference
            ?? throw new DevelopmentInvalidTransitionException("The approved manifest has no managed artifact reference."),
            current.PatchArtifact.Id,
            current.ManifestArtifact.Id,
            current.Current.SubjectHash,
            project.RepositoryIdentityHash,
            project.BaseBranch,
            current.PatchArtifact.ByteCount,
            current.ManifestArtifact.ByteCount);
        return new DevelopmentPatchPreview(subject,
            Encoding.UTF8.GetString(current.Patch.Span),
            current.Current.ChangedFiles);
    }

    public async Task<DevelopmentOperationResult> ApplyAsync(Guid taskId,
        Guid operationId,
        DevelopmentRepositoryBinding repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var task = await _store.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        var completed = await _store.FindOperationAsync(task.ProjectId,
            operationId,
            DevelopmentOperationPhases.ApplyCompleted,
            cancellationToken).ConfigureAwait(false);
        if (completed is not null)
        {
            return completed;
        }

        var blocked = await _store.FindOperationAsync(task.ProjectId,
            operationId,
            DevelopmentOperationPhases.ApplyBlocked,
            cancellationToken).ConfigureAwait(false);
        if (blocked is not null)
        {
            return blocked;
        }

        var preview = await PreviewAsync(taskId, repository, cancellationToken).ConfigureAwait(false);
        return await _coordinator.ApplyRevalidatedAsync(operationId,
            preview.Subject,
            repository,
            async revalidationToken =>
            {
                var revalidatedRepository = await _repositoryBindings.ResolveProjectAsync(preview.Subject.ProjectId, revalidationToken).ConfigureAwait(false);
                var revalidated = await PreviewAsync(taskId, revalidatedRepository, revalidationToken).ConfigureAwait(false);
                if (!Equals(preview.Subject, revalidated.Subject))
                {
                    throw new DevelopmentInvalidTransitionException("The exact approved workspace subject changed before host mutation.");
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(DevelopmentArtifactSnapshot Artifact, DevelopmentValidationReport Report)> ReadValidationAsync(Guid taskId,
        DevelopmentEvidenceSet current,
        string expectedProfileDigest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProfileDigest);
        var (artifact, content) = await _evidence.ReadLatestAsync(taskId,
            DevelopmentArtifactKind.ValidationReport,
            cancellationToken).ConfigureAwait(false);
        var report = JsonSerializer.Deserialize<DevelopmentValidationReport>(content.Span, JsonOptions)
                     ?? throw new DevelopmentInvalidTransitionException("The approved validation report is invalid.");
        EnsureEvidenceMatches(artifact, report.BaseCommit, report.SubjectHash, report.ManifestHash, current);
        var latestCoder = (await _store.ListAttemptsAsync(taskId, cancellationToken).ConfigureAwait(false))
            .LastOrDefault(attempt => attempt.Role == DevelopmentAttemptRole.Coder
                                      && attempt.Status == DevelopmentAttemptStatus.Succeeded);
        if (!report.Passed

            // Artifact protocol version, unchanged — it gates report shape compatibility.
            || !string.Equals(report.CommandProfileVersion, DevelopmentValidationRunner.ProfileVersion, StringComparison.Ordinal)
            || !string.Equals(artifact.CommandProfileVersion, DevelopmentValidationRunner.ProfileVersion, StringComparison.Ordinal)

            // Command-profile digest — an independent dimension gating which commands produced the approval.
            || !string.Equals(report.CommandProfileDigest, expectedProfileDigest, StringComparison.Ordinal)
            || !string.Equals(artifact.CommandProfileDigest, expectedProfileDigest, StringComparison.Ordinal)
            || !string.Equals(report.ExpectedResultHash, current.Current.ExpectedResultHash, StringComparison.OrdinalIgnoreCase)
            || artifact.AttemptId != current.PatchArtifact.AttemptId
            || artifact.AttemptId != current.ManifestArtifact.AttemptId
            || latestCoder is null
            || artifact.AttemptId != latestCoder.Id)
        {
            throw new DevelopmentInvalidTransitionException("The validation report does not authorize the exact current result.");
        }

        EnsureInputs(artifact, current.PatchArtifact.Id, current.ManifestArtifact.Id);
        return (artifact, report);
    }

    private async Task<(DevelopmentArtifactSnapshot Artifact, DevelopmentReviewReport Report)> ReadReviewAsync(Guid taskId,
        DevelopmentEvidenceSet current,
        Guid validationArtifactId,
        string expectedProfileDigest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProfileDigest);
        var (artifact, content) = await _evidence.ReadLatestAsync(taskId,
            DevelopmentArtifactKind.ReviewReport,
            cancellationToken).ConfigureAwait(false);
        var report = JsonSerializer.Deserialize<DevelopmentReviewReport>(content.Span, JsonOptions)
                     ?? throw new DevelopmentInvalidTransitionException("The approved review report is invalid.");
        EnsureEvidenceMatches(artifact, report.BaseCommit, report.SubjectHash, report.ManifestHash, current);
        if (report.Disposition != DevelopmentReviewDisposition.Approved
            || report.ValidationArtifactId != validationArtifactId
            || !string.Equals(report.ExpectedResultHash, current.Current.ExpectedResultHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(artifact.CommandProfileVersion, DevelopmentReviewerAttemptRunner.ProfileVersion, StringComparison.Ordinal)
            || !string.Equals(artifact.CommandProfileDigest, expectedProfileDigest, StringComparison.Ordinal))
        {
            throw new DevelopmentInvalidTransitionException("The review report does not independently approve the exact current result.");
        }

        EnsureInputs(artifact, current.PatchArtifact.Id, current.ManifestArtifact.Id, validationArtifactId);
        return (artifact, report);
    }

    private static void EnsureEvidenceMatches(DevelopmentArtifactSnapshot artifact,
        string baseCommit,
        string subjectHash,
        string manifestHash,
        DevelopmentEvidenceSet current)
    {
        if (!string.Equals(baseCommit, current.Current.BaseCommit, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(subjectHash, current.Current.SubjectHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifestHash, current.Current.ManifestHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(artifact.BaseCommit, current.Current.BaseCommit, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(artifact.SubjectHash, current.Current.SubjectHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(artifact.ChangedFilesManifestHash, current.Current.ManifestHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new DevelopmentInvalidTransitionException("The approval evidence is stale for the current workspace subject.");
        }
    }

    private static void EnsureInputs(DevelopmentArtifactSnapshot artifact, params Guid[] expected)
    {
        var inputs = artifact.InputArtifactIdsJson is { } json
            ? JsonSerializer.Deserialize<Guid[]>(json.Span, JsonOptions) ?? []
            : [];
        if (inputs.Length != expected.Length || !inputs.ToHashSet().SetEquals(expected))
        {
            throw new DevelopmentInvalidTransitionException("The approval artifact is not bound to the exact required input artifacts.");
        }
    }
}
