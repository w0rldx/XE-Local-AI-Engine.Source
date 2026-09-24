namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Sandbox;

internal interface IDevelopmentReviewerAttemptRunner
{
    Task<DevelopmentReviewerAttemptResult> RunAsync(Guid attemptId,
        DevelopmentRepositoryBinding repository,
        CancellationToken cancellationToken = default);
}

internal sealed class DevelopmentReviewerAttemptRunner : IDevelopmentReviewerAttemptRunner
{
    internal const string ProfileVersion = "development-review-v1";

    /// <summary>
    ///     What the reviewer's own line to the next round may weigh, matching the bound the workflow's routed change
    ///     requests carry. A rework brief is a sentence to act on, not a second copy of the review report.
    /// </summary>
    private const int MaxChangeRequestReason = 4096;

    private const string GenericChangeRequest = "The independent reviewer requested changes.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDevelopmentEvidenceService _evidence;
    private readonly IDevelopmentCloudAttemptContextService _cloudContext;
    private readonly IDevelopmentAttemptLiveBroker? _liveBroker;
    private readonly ILogger<DevelopmentReviewerAttemptRunner> _logger;
    private readonly DevelopmentOptions _options;
    private readonly IDevelopmentReviewerModel _reviewerModel;
    private readonly IDevelopmentSandboxRuntimeProvider _sandbox;
    private readonly IDevelopmentStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly IDevelopmentWorkspaceProvider _workspaceProvider;

    public DevelopmentReviewerAttemptRunner(IDevelopmentStore store,
        IDevelopmentWorkspaceProvider workspaceProvider,
        IDevelopmentSandboxRuntimeProvider sandbox,
        IDevelopmentEvidenceService evidence,
        IDevelopmentReviewerModel reviewerModel,
        IDevelopmentCloudAttemptContextService cloudContext,
        IOptions<DevelopmentOptions> options,
        TimeProvider timeProvider,
        ILogger<DevelopmentReviewerAttemptRunner> logger,
        IDevelopmentAttemptLiveBroker? liveBroker = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        var snapshot = await _store.GetExecutionSnapshotAsync(attemptId, cancellationToken);
        EnsureRunnable(snapshot);
        var profile = DevelopmentCommandProfileCatalog.ResolveStored(snapshot.CommandProfileJson);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(snapshot.MaxDurationSeconds ?? _options.MaxAttemptDurationSeconds,
            _options.MaxAttemptDurationSeconds)));

        try
        {
            var task = await _store.GetTaskAsync(snapshot.TaskId, timeout.Token);
            var session = await _workspaceProvider.PrepareAsync(snapshot, repository, timeout.Token);
            var evidence = await _evidence.ResolveCurrentAsync(snapshot.TaskId, session, timeout.Token);
            DevelopmentArtifactWith<DevelopmentValidationReport> validation;
            try
            {
                validation = await ReadValidationAsync(snapshot.TaskId, evidence, profile.ComputeDigest(), timeout.Token);
            }
            catch (DevelopmentInvalidTransitionException)
            {
                await _evidence.InvalidateApprovalEvidenceAsync(snapshot.TaskId,
                    "The exact Development validation evidence is no longer authoritative.",
                    CancellationToken.None);
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
            var tools = new DevelopmentWorkspaceTools(_sandbox, session, Options.Create(_options), profile, liveProgress);
            var cloudContext = await CreateCloudContextAsync(snapshot,
                evidence,
                validationArtifact,
                validationReport,
                timeout.Token);
            var protectedRoots = DevelopmentArtifactSanitizer.ResolveProtectedRoots(repository.RepositoryRoot, session);
            var prompt = BuildPrompt(snapshot, task, validationReport, profile);
            await PersistPromptAsync(snapshot,
                prompt,
                evidence,
                validationArtifact.Id,
                profile,
                protectedRoots);
            var model = await _reviewerModel.RunAsync(snapshot.ModelId,
                prompt,
                tools,
                maxOutputTokens,
                _options.MaxToolCalls,
                liveProgress,
                cloudContext?.Route,
                timeout.Token);
            var submission = DevelopmentArtifactSanitizer.Sanitize(model.Submission, protectedRoots);

            var afterReview = await _evidence.ResolveCurrentAsync(snapshot.TaskId, session, timeout.Token);
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
                profile.ComputeDigest(),
                timeout.Token);

            var target = submission.Disposition == DevelopmentReviewDisposition.Approved
                ? DevelopmentTaskStatus.AwaitingApply
                : DevelopmentTaskStatus.ChangesRequested;
            _ = await _store.FinalizeReviewAsync(new DevelopmentFinalizeReviewCommand
                {
                    Artifact = prepared.Attachment,
                    OperationId = Guid.NewGuid(),
                    ExpectedTaskVersion = snapshot.TaskVersion,
                    ExpectedAttemptVersion = snapshot.AttemptVersion,
                    TargetStatus = target,
                    ApprovedSubjectHash = target == DevelopmentTaskStatus.AwaitingApply ? evidence.Current.SubjectHash : null,
                    SanitizedReason = submission.Disposition == DevelopmentReviewDisposition.ChangesRequested
                        ? ChangeRequestReason(submission)
                        : null,
                    InputTokens = model.InputTokens,
                    OutputTokens = model.OutputTokens
                },
                CancellationToken.None);
            return new DevelopmentReviewerAttemptResult
            {
                AttemptId = snapshot.AttemptId,
                ArtifactId = prepared.ArtifactId,
                Disposition = submission.Disposition,
                TaskStatus = target,
                SubjectHash = evidence.Current.SubjectHash
            };
        }
        catch (Exception exception)
        {
            try
            {
                _ = await _store.TerminalizeAttemptAsync(new DevelopmentTerminalizeAttemptCommand
                    {
                        AttemptId = snapshot.AttemptId,
                        OperationId = Guid.NewGuid(),
                        Status = exception is OperationCanceledException
                            ? DevelopmentAttemptStatus.Cancelled
                            : DevelopmentAttemptStatus.Failed,
                        ExpectedAttemptVersion = snapshot.AttemptVersion,
                        TerminalReason = SanitizedReason(exception)
                    },
                    CancellationToken.None);
            }
            catch (DevelopmentInvalidTransitionException)
            {
                // A concurrent terminal action already won; preserve the original reviewer failure.
            }

            throw;
        }
    }

    /// <summary>Records what this round told the reviewer, before the reviewer is called.</summary>
    /// <remarks>
    ///     Unlike the coder's, this prompt names a subject that already exists, so it carries the full patch evidence
    ///     and cites the artifacts it was built from. Best-effort and under <see cref="CancellationToken.None" /> for
    ///     the coder's reasons: a review ending in a cancelled attempt or a rejected submission is exactly the one
    ///     whose prompt is worth having, and an observation must never fail the attempt it observes. Swallows log.
    /// </remarks>
    private async Task PersistPromptAsync(DevelopmentExecutionSnapshot snapshot,
        string prompt,
        DevelopmentEvidenceSet evidence,
        Guid validationArtifactId,
        DevelopmentCommandProfile profile,
        string[] protectedRoots)
    {
        try
        {
            var sanitized = DevelopmentArtifactSanitizer.SanitizePromptText(prompt, profile.ProtectedPaths, protectedRoots);
            var prepared = await _evidence.PrepareAsync(snapshot,
                DevelopmentArtifactKind.Prompt,
                Encoding.UTF8.GetBytes(sanitized),
                evidence.Current,
                [evidence.PatchArtifact.Id, evidence.ManifestArtifact.Id, validationArtifactId],
                ProfileVersion,
                profile.ComputeDigest(),
                CancellationToken.None);
            _ = await _store.AttachArtifactAsync(prepared.Attachment, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Development reviewer prompt for attempt {AttemptId} on task {TaskId} was not recorded.",
                snapshot.AttemptId,
                snapshot.TaskId);
        }
    }

    private async Task<DevelopmentArtifactWith<DevelopmentValidationReport>> ReadValidationAsync(Guid taskId,
        DevelopmentEvidenceSet evidence,
        string expectedProfileDigest,
        CancellationToken cancellationToken)
    {
        var (validationArtifact, validationContent) = await _evidence.ReadLatestAsync(taskId,
            DevelopmentArtifactKind.ValidationReport,
            cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProfileDigest);
        var report = JsonSerializer.Deserialize<DevelopmentValidationReport>(validationContent.Span, JsonOptions)
                     ?? throw new DevelopmentInvalidTransitionException("The validation report artifact is invalid.");
        var inputIds = validationArtifact.InputArtifactIdsJson is { } json
            ? JsonSerializer.Deserialize<Guid[]>(json.Span, JsonOptions) ?? []
            : [];
        var latestCoder = (await _store.ListAttemptsAsync(taskId, cancellationToken))
            .LastOrDefault(attempt => attempt.Role == DevelopmentAttemptRole.Coder
                                      && attempt.Status == DevelopmentAttemptStatus.Succeeded);
        if (!report.Passed

            // The two artifact protocol checks prove the report has the shape this reviewer understands. The digest
            // checks below are an added dimension, not a replacement: a digest says nothing about parseability.
            || !string.Equals(report.CommandProfileVersion, DevelopmentValidationRunner.ProfileVersion, StringComparison.Ordinal)
            || !string.Equals(validationArtifact.CommandProfileVersion, DevelopmentValidationRunner.ProfileVersion, StringComparison.Ordinal)

            // The report must come from the profile this project runs under now, recorded in the body and on the row,
            // so a profile change invalidates stale approval instead of approving commands that would no longer run.
            || !string.Equals(report.CommandProfileDigest, expectedProfileDigest, StringComparison.Ordinal)
            || !string.Equals(validationArtifact.CommandProfileDigest, expectedProfileDigest, StringComparison.Ordinal)
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

        return new DevelopmentArtifactWith<DevelopmentValidationReport>(validationArtifact, report);
    }

    private async Task<DevelopmentCloudAttemptContext?> CreateCloudContextAsync(DevelopmentExecutionSnapshot snapshot,
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
                new DevelopmentCloudContextExcerpt
                {
                    RelativePath = "workspace.patch",
                    Content = Encoding.UTF8.GetString(evidence.Patch.Span)
                },
                new DevelopmentCloudContextExcerpt
                {
                    RelativePath = "changed-files.json",
                    Content = Encoding.UTF8.GetString(evidence.Manifest.Span)
                },
                new DevelopmentCloudContextExcerpt
                {
                    RelativePath = "validation-report.json",
                    Content = JsonSerializer.Serialize(validationReport, JsonOptions)
                }
            ],
            [evidence.PatchArtifact.Id, evidence.ManifestArtifact.Id, validationArtifact.Id],
            cancellationToken);
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

    /// <summary>Internal so the composition can be pinned directly; nothing outside this class calls it.</summary>
    internal static string BuildPrompt(DevelopmentExecutionSnapshot snapshot,
        DevelopmentTaskSnapshot task,
        DevelopmentValidationReport validation,
        DevelopmentCommandProfile profile) =>
        string.Concat("Task: ", snapshot.Title,
            "\nRequirements:\n", snapshot.Requirements,
            "\nAcceptance criteria:\n", snapshot.AcceptanceCriteriaJson,
            "\n", DevelopmentTestWritePolicy.Prompt(profile),
            Policy(snapshot.WorkflowPolicyText),
            OperatorInstruction(snapshot.OperatorInstruction),
            "\nRound: ", task.CurrentReviewRound, " of ", task.MaxReviewRounds,
            "\nValidated subject: ", validation.SubjectHash,
            "\nValidation profile: ", validation.CommandProfileVersion,
            "\nValidation passed: ", validation.Passed,
            DescribeTestResults(validation),
            "\nJudge the work against the requirements AS AMENDED by any operator instruction above, and never request a change the test-write policy forbids.",
            "\nUse only the read-only tools. Never modify the worktree or claim task completion.");

    /// <summary>The rule sets a Development workflow resolved for the node run driving this task, when one does.</summary>
    /// <remarks>
    ///     Rendered and bounded by the workflow before it reached the task, so this only decides whether there is a
    ///     section at all: an empty heading governs nothing and would read as a policy that said nothing.
    /// </remarks>
    private static string Policy(string? workflowPolicy) =>
        string.IsNullOrWhiteSpace(workflowPolicy)
            ? string.Empty
            : string.Concat("\nPolicy (rule sets applied by the workflow):\n", workflowPolicy);

    /// <summary>Puts the gate's structured test counts in front of the reviewer.</summary>
    /// <remarks>
    ///     "Validation passed" alone does not let a reviewer tell a change covered by twenty executed tests from one
    ///     covered by two, and coverage of the change is exactly the judgement the review round exists to make.
    /// </remarks>
    private static string DescribeTestResults(DevelopmentValidationReport validation)
    {
        var outcomes = validation.Commands
                                 .Where(static command => command.TestOutcome is not null)
                                 .Select(static command => (command.CommandId, Outcome: command.TestOutcome!))
                                 .ToArray();
        if (outcomes.Length == 0)
        {
            return string.Empty;
        }

        return "\nTest results:\n"
               + string.Join('\n',
                   outcomes.Select(static entry => entry.Outcome.Parsed
                       ? $"- {entry.CommandId}: {entry.Outcome.Executed} executed, {entry.Outcome.Passed} passed, {entry.Outcome.Failed} failed, {entry.Outcome.Discovered} discovered"
                       : $"- {entry.CommandId}: no readable test result ({entry.Outcome.ParseFailureCode})"));
    }

    /// <summary>What a person told this task to do differently, rendered for the reviewer as well as the coder.</summary>
    /// <remarks>
    ///     The asymmetry deadlocks a task: an operator moves a test out of a base-committed file the test-write policy
    ///     protects, the coder complies and validation passes, and a reviewer reading only the original requirements
    ///     demands it back — whereupon the next coder round obeys and is refused by the policy, for every retry.
    /// </remarks>
    private static string OperatorInstruction(string? instruction) =>
        string.IsNullOrWhiteSpace(instruction)
            ? string.Empty
            : string.Concat("\nOperator instruction. This AMENDS the requirements and the acceptance criteria above, wherever they conflict:\n",
                instruction,
                "\nWork implementing it is not a requirement violation on that point; judge everything else as usual.",
                "\nIt does not amend the workspace test-write policy, which is enforced and cannot be waived.");

    /// <summary>The workspace policy's message, or the generic line when it cannot be shown safely.</summary>
    private static string PolicyReason(DevelopmentWorkspaceSecurityException exception)
    {
        try
        {
            return DevelopmentArtifactSanitizer.SanitizeText(exception.Message);
        }
        catch (DevelopmentWorkspaceSecurityException)
        {
            return "The Development reviewer attempt violated a workspace security policy.";
        }
    }

    /// <summary>Why the reviewer asked for another round, in its own words.</summary>
    /// <remarks>
    ///     The fixed sentence alone tells the next coder round nothing to act on, so it re-implements blind. The
    ///     findings arrive already sanitized against this attempt's protected roots
    ///     (<see cref="DevelopmentArtifactSanitizer.Sanitize(DevelopmentReviewerSubmission, string[])" />), so the
    ///     second pass over the joined text is belt and braces: a refusal falls back to the fixed sentence rather
    ///     than escaping, and never costs a review that otherwise succeeded.
    /// </remarks>
    internal static string ChangeRequestReason(DevelopmentReviewerSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var findings = submission.Findings
                                 .Where(static finding => !string.IsNullOrWhiteSpace(finding.Summary))
                                 .Select(static finding => $"- {finding.Category}: {finding.Summary}")
                                 .ToArray();
        if (findings.Length == 0)
        {
            return GenericChangeRequest;
        }

        try
        {
            var composed = DevelopmentArtifactSanitizer.SanitizeText(string.Join('\n', [GenericChangeRequest, .. findings]));
            return composed.Length <= MaxChangeRequestReason ? composed : composed[..MaxChangeRequestReason];
        }
        catch (DevelopmentWorkspaceSecurityException)
        {
            return GenericChangeRequest;
        }
    }

    /// <summary>Internal so the policy line can be pinned directly; nothing outside this class calls it.</summary>
    internal static string SanitizedReason(Exception exception) =>
        exception switch
        {
            OperationCanceledException => "The bounded Development reviewer attempt was cancelled or timed out.",

            // Mirrors DevelopmentCoderAttemptRunner.SanitizedReason: the policy's own sentence behind the same failure
            // code, so a refusal names its rule and a workflow node stands down rather than spending its retry budget.
            DevelopmentWorkspaceSecurityException security => DevelopmentAttemptEvidenceException.Compose(DevelopmentAttemptFailureCodes.WorkspacePolicyRefused,
                PolicyReason(security)),

            // See DevelopmentCoderAttemptRunner.SanitizedReason: this message is engine-authored, so it is safe to
            // surface verbatim rather than replacing a diagnosed failure with a generic one.
            DevelopmentAttemptEvidenceException evidence => evidence.TerminalReason,
            _ => "The bounded Development reviewer attempt failed before producing valid exact evidence."
        };
}
