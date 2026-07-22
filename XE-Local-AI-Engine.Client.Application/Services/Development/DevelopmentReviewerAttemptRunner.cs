namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Sandbox;

internal sealed record DevelopmentReviewReport(
    DevelopmentReviewDisposition Disposition,
    string Summary,
    IReadOnlyList<DevelopmentReviewFinding> Findings,
    int ReviewRound,
    string BaseCommit,
    string SubjectHash,
    string ManifestHash,
    string ExpectedResultHash,
    Guid ValidationArtifactId,
    long CompletedAtUtc);

internal sealed record DevelopmentReviewerAttemptResult(
    Guid AttemptId,
    Guid ArtifactId,
    DevelopmentReviewDisposition Disposition,
    DevelopmentTaskStatus TaskStatus,
    string SubjectHash);

internal interface IDevelopmentReviewerAttemptRunner
{
    Task<DevelopmentReviewerAttemptResult> RunAsync(Guid attemptId,
        DevelopmentRepositoryBinding repository,
        CancellationToken cancellationToken = default);
}

internal sealed class DevelopmentReviewerAttemptRunner : IDevelopmentReviewerAttemptRunner
{
    internal const string ProfileVersion = "development-review-v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDevelopmentEvidenceService _evidence;
    private readonly IDevelopmentCloudAttemptContextService _cloudContext;
    private readonly IDevelopmentAttemptLiveBroker? _liveBroker;
    private readonly DevelopmentOptions _options;
    private readonly IDevelopmentReviewerModel _reviewerModel;
    private readonly ISandboxRuntimeProvider _sandbox;
    private readonly IDevelopmentStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly IDevelopmentWorkspaceProvider _workspaceProvider;

    public DevelopmentReviewerAttemptRunner(IDevelopmentStore store,
        IDevelopmentWorkspaceProvider workspaceProvider,
        ISandboxRuntimeProvider sandbox,
        IDevelopmentEvidenceService evidence,
        IDevelopmentReviewerModel reviewerModel,
        IDevelopmentCloudAttemptContextService cloudContext,
        IOptions<DevelopmentOptions> options,
        TimeProvider timeProvider,
        IDevelopmentAttemptLiveBroker? liveBroker = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workspaceProvider = workspaceProvider ?? throw new ArgumentNullException(nameof(workspaceProvider));
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _reviewerModel = reviewerModel ?? throw new ArgumentNullException(nameof(reviewerModel));
        _cloudContext = cloudContext ?? throw new ArgumentNullException(nameof(cloudContext));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _liveBroker = liveBroker;
    }

    public async Task<DevelopmentReviewerAttemptResult> RunAsync(Guid attemptId,
        DevelopmentRepositoryBinding repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var snapshot = await _store.GetExecutionSnapshotAsync(attemptId, cancellationToken).ConfigureAwait(false);
        EnsureRunnable(snapshot);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(snapshot.MaxDurationSeconds ?? _options.MaxAttemptDurationSeconds,
            _options.MaxAttemptDurationSeconds)));

        try
        {
            var task = await _store.GetTaskAsync(snapshot.TaskId, timeout.Token).ConfigureAwait(false);
            var session = await _workspaceProvider.PrepareAsync(snapshot, repository, timeout.Token).ConfigureAwait(false);
            var evidence = await _evidence.ResolveCurrentAsync(snapshot.TaskId, session, timeout.Token).ConfigureAwait(false);
            (DevelopmentArtifactSnapshot validationArtifact, DevelopmentValidationReport validationReport) validation;
            try
            {
                validation = await ReadValidationAsync(snapshot.TaskId, evidence, timeout.Token).ConfigureAwait(false);
            }
            catch (DevelopmentInvalidTransitionException)
            {
                await _evidence.InvalidateApprovalEvidenceAsync(snapshot.TaskId,
                    "The exact Development validation evidence is no longer authoritative.",
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            var (validationArtifact, validationReport) = validation;
            var maxOutputTokens = Math.Min(snapshot.MaxTokens ?? _options.MaxOutputTokens, _options.MaxOutputTokens);
            var liveProgress = _liveBroker is null
                ? null
                : new DevelopmentAttemptLiveProgress(snapshot,
                    _liveBroker,
                    Options.Create(_options),
                    _timeProvider,
                    maxOutputTokens,
                    _options.MaxToolCalls);
            var tools = new DevelopmentWorkspaceTools(_sandbox, session, Options.Create(_options), liveProgress);
            var cloudContext = await CreateCloudContextAsync(snapshot,
                evidence,
                validationArtifact,
                validationReport,
                timeout.Token).ConfigureAwait(false);
            var model = await _reviewerModel.RunAsync(snapshot.ModelId,
                BuildPrompt(snapshot, task, validationReport),
                tools,
                maxOutputTokens,
                _options.MaxToolCalls,
                liveProgress,
                cloudContext?.Route,
                timeout.Token).ConfigureAwait(false);
            var submission = DevelopmentArtifactSanitizer.Sanitize(model.Submission,
                repository.RepositoryRoot,
                session.HostWorktreePath,
                session.RuntimePath);

            var afterReview = await _evidence.ResolveCurrentAsync(snapshot.TaskId, session, timeout.Token).ConfigureAwait(false);
            if (!string.Equals(evidence.Current.SubjectHash, afterReview.Current.SubjectHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new DevelopmentWorkspaceSecurityException("The workspace subject changed during a read-only review attempt.");
            }

            var report = new DevelopmentReviewReport(submission.Disposition,
                submission.Summary,
                submission.Findings,
                task.CurrentReviewRound,
                evidence.Current.BaseCommit,
                evidence.Current.SubjectHash,
                evidence.Current.ManifestHash,
                evidence.Current.ExpectedResultHash,
                validationArtifact.Id,
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            liveProgress?.ReviewObserved(task.CurrentReviewRound, submission, evidence.Current.SubjectHash);
            IReadOnlyList<Guid> reviewInputArtifactIds = cloudContext is null
                ? [evidence.PatchArtifact.Id, evidence.ManifestArtifact.Id, validationArtifact.Id]
                : [evidence.PatchArtifact.Id, evidence.ManifestArtifact.Id, validationArtifact.Id, cloudContext.ArtifactId];
            var prepared = await _evidence.PrepareAsync(snapshot,
                DevelopmentArtifactKind.ReviewReport,
                JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions),
                evidence.Current,
                reviewInputArtifactIds,
                ProfileVersion,
                timeout.Token).ConfigureAwait(false);

            var target = submission.Disposition == DevelopmentReviewDisposition.Approved
                ? DevelopmentTaskStatus.AwaitingApply
                : DevelopmentTaskStatus.ChangesRequested;
            _ = await _store.FinalizeReviewAsync(new DevelopmentFinalizeReviewCommand(prepared.Attachment,
                                                    Guid.NewGuid(),
                                                    snapshot.TaskVersion,
                                                    snapshot.AttemptVersion,
                                                    target,
                                                    target == DevelopmentTaskStatus.AwaitingApply ? evidence.Current.SubjectHash : null,
                                                    submission.Disposition == DevelopmentReviewDisposition.ChangesRequested
                                                        ? "The independent reviewer requested changes."
                                                        : null,
                                                    model.InputTokens,
                                                    model.OutputTokens),
                                                CancellationToken.None)
                            .ConfigureAwait(false);
            return new DevelopmentReviewerAttemptResult(snapshot.AttemptId,
                prepared.ArtifactId,
                submission.Disposition,
                target,
                evidence.Current.SubjectHash);
        }
        catch (Exception exception)
        {
            try
            {
                _ = await _store.TerminalizeAttemptAsync(new DevelopmentTerminalizeAttemptCommand(snapshot.AttemptId,
                                                            Guid.NewGuid(),
                                                            exception is OperationCanceledException
                                                                ? DevelopmentAttemptStatus.Cancelled
                                                                : DevelopmentAttemptStatus.Failed,
                                                            snapshot.AttemptVersion,
                                                            SanitizedReason(exception)),
                                                        CancellationToken.None)
                                .ConfigureAwait(false);
            }
            catch (DevelopmentInvalidTransitionException)
            {
                // A concurrent terminal action already won; preserve the original reviewer failure.
            }

            throw;
        }
    }

    private async Task<(DevelopmentArtifactSnapshot Artifact, DevelopmentValidationReport Report)> ReadValidationAsync(Guid taskId,
        DevelopmentEvidenceSet evidence,
        CancellationToken cancellationToken)
    {
        var (validationArtifact, validationContent) = await _evidence.ReadLatestAsync(taskId,
            DevelopmentArtifactKind.ValidationReport,
            cancellationToken).ConfigureAwait(false);
        var report = JsonSerializer.Deserialize<DevelopmentValidationReport>(validationContent.Span, JsonOptions)
                     ?? throw new DevelopmentInvalidTransitionException("The validation report artifact is invalid.");
        var inputIds = validationArtifact.InputArtifactIdsJson is { } json
            ? JsonSerializer.Deserialize<Guid[]>(json.Span, JsonOptions) ?? []
            : [];
        var latestCoder = (await _store.ListAttemptsAsync(taskId, cancellationToken).ConfigureAwait(false))
                          .LastOrDefault(attempt => attempt.Role == DevelopmentAttemptRole.Coder
                                                    && attempt.Status == DevelopmentAttemptStatus.Succeeded);
        if (!report.Passed
            || !string.Equals(report.CommandProfileVersion, DevelopmentValidationRunner.ProfileVersion, StringComparison.Ordinal)
            || !string.Equals(validationArtifact.CommandProfileVersion, DevelopmentValidationRunner.ProfileVersion, StringComparison.Ordinal)
            || !string.Equals(report.BaseCommit, evidence.Current.BaseCommit, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(report.SubjectHash, evidence.Current.SubjectHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(report.ManifestHash, evidence.Current.ManifestHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(report.ExpectedResultHash, evidence.Current.ExpectedResultHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(validationArtifact.BaseCommit, evidence.Current.BaseCommit, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(validationArtifact.SubjectHash, evidence.Current.SubjectHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(validationArtifact.ChangedFilesManifestHash, evidence.Current.ManifestHash, StringComparison.OrdinalIgnoreCase)
            || validationArtifact.AttemptId != evidence.PatchArtifact.AttemptId
            || validationArtifact.AttemptId != evidence.ManifestArtifact.AttemptId
            || latestCoder is null
            || validationArtifact.AttemptId != latestCoder.Id
            || inputIds.Length != 2
            || !inputIds.ToHashSet().SetEquals([evidence.PatchArtifact.Id, evidence.ManifestArtifact.Id]))
        {
            throw new DevelopmentInvalidTransitionException("The validation report does not authorize the current exact workspace subject.");
        }

        return (validationArtifact, report);
    }

    private async Task<DevelopmentCloudAttemptContext?> CreateCloudContextAsync(
        DevelopmentExecutionSnapshot snapshot,
        DevelopmentEvidenceSet evidence,
        DevelopmentArtifactSnapshot validationArtifact,
        DevelopmentValidationReport validationReport,
        CancellationToken cancellationToken)
    {
        if (string.Equals(snapshot.Provider, "local", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return await _cloudContext.CreateAsync(snapshot,
            [
                new DevelopmentCloudContextExcerpt("workspace.patch", System.Text.Encoding.UTF8.GetString(evidence.Patch.Span)),
                new DevelopmentCloudContextExcerpt("changed-files.json", System.Text.Encoding.UTF8.GetString(evidence.Manifest.Span)),
                new DevelopmentCloudContextExcerpt("validation-report.json", JsonSerializer.Serialize(validationReport, JsonOptions))
            ],
            [evidence.PatchArtifact.Id, evidence.ManifestArtifact.Id, validationArtifact.Id],
            cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureRunnable(DevelopmentExecutionSnapshot snapshot)
    {
        if (snapshot.AttemptRole != DevelopmentAttemptRole.Reviewer
            || snapshot.AttemptStatus != DevelopmentAttemptStatus.Running
            || snapshot.TaskStatus != DevelopmentTaskStatus.InReview
            || snapshot.EgressPolicy is not (DevelopmentEgressPolicy.LocalOnly or DevelopmentEgressPolicy.CloudScoped)
            || string.IsNullOrWhiteSpace(snapshot.ModelId)
            || string.IsNullOrWhiteSpace(snapshot.Provider)
            || (snapshot.EgressPolicy == DevelopmentEgressPolicy.LocalOnly
                && !string.Equals(snapshot.Provider, "local", StringComparison.OrdinalIgnoreCase)))
        {
            throw new DevelopmentInvalidTransitionException("Only one running reviewer attempt in review with a valid egress policy and explicit model/provider can execute.");
        }
    }

    private static string BuildPrompt(DevelopmentExecutionSnapshot snapshot,
        DevelopmentTaskSnapshot task,
        DevelopmentValidationReport validation)
        => string.Concat("Task: ", snapshot.Title,
            "\nRequirements:\n", snapshot.Requirements,
            "\nAcceptance criteria:\n", snapshot.AcceptanceCriteriaJson,
            "\nReview round: ", task.CurrentReviewRound, " of ", task.MaxReviewRounds,
            "\nValidated subject: ", validation.SubjectHash,
            "\nValidation profile: ", validation.CommandProfileVersion,
            "\nValidation passed: ", validation.Passed,
            "\nUse only the read-only tools. Never modify the worktree or claim task completion.");

    private static string SanitizedReason(Exception exception)
        => exception switch
        {
            OperationCanceledException => "The bounded Development reviewer attempt was cancelled or timed out.",
            DevelopmentWorkspaceSecurityException => "The Development reviewer attempt violated a workspace security policy.",
            _ => "The bounded Development reviewer attempt failed before producing valid exact evidence."
        };
}
