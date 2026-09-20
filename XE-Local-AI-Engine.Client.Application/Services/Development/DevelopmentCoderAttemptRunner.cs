namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Sandbox;

internal sealed class DevelopmentCoderAttemptResult
{
    public required Guid AttemptId { get; init; }

    public required string BaseCommit { get; init; }

    public required string SubjectHash { get; init; }

    public required string PatchHash { get; init; }

    public required string ManifestHash { get; init; }

    public required IReadOnlyList<string> ChangedFiles { get; init; }
}

internal interface IDevelopmentCoderAttemptRunner
{
    Task<DevelopmentCoderAttemptResult> RunAsync(Guid attemptId,
        DevelopmentRepositoryBinding repository,
        CancellationToken cancellationToken = default);
}

internal sealed class DevelopmentCoderAttemptRunner : IDevelopmentCoderAttemptRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDevelopmentArtifactBlobStore _blobStore;
    private readonly IDevelopmentCloudAttemptContextService _cloudContext;
    private readonly IDevelopmentCoderModel _coderModel;
    private readonly IDevelopmentAttemptLiveBroker? _liveBroker;
    private readonly ILogger<DevelopmentCoderAttemptRunner> _logger;
    private readonly DevelopmentOptions _options;
    private readonly IDevelopmentPatchEvidenceService _patchEvidence;
    private readonly IDevelopmentSandboxRuntimeProvider _sandbox;
    private readonly IDevelopmentStore _store;
    private readonly IDevelopmentWorkspaceProvider _workspaceProvider;
    private readonly TimeProvider _timeProvider;

    public DevelopmentCoderAttemptRunner(IDevelopmentStore store,
        IDevelopmentWorkspaceProvider workspaceProvider,
        IDevelopmentSandboxRuntimeProvider sandbox,
        IDevelopmentPatchEvidenceService patchEvidence,
        IDevelopmentArtifactBlobStore blobStore,
        IDevelopmentCoderModel coderModel,
        IDevelopmentCloudAttemptContextService cloudContext,
        IOptions<DevelopmentOptions> options,
        ILogger<DevelopmentCoderAttemptRunner> logger,
        TimeProvider timeProvider,
        IDevelopmentAttemptLiveBroker? liveBroker = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workspaceProvider = workspaceProvider ?? throw new ArgumentNullException(nameof(workspaceProvider));
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        _patchEvidence = patchEvidence ?? throw new ArgumentNullException(nameof(patchEvidence));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        _coderModel = coderModel ?? throw new ArgumentNullException(nameof(coderModel));
        _cloudContext = cloudContext ?? throw new ArgumentNullException(nameof(cloudContext));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _liveBroker = liveBroker;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<DevelopmentCoderAttemptResult> RunAsync(Guid attemptId,
        DevelopmentRepositoryBinding repository,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _store.GetExecutionSnapshotAsync(attemptId, cancellationToken);
        EnsureRunnable(snapshot);
        var profile = DevelopmentCommandProfileCatalog.ResolveStored(snapshot.CommandProfileJson);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(snapshot.MaxDurationSeconds ?? _options.MaxAttemptDurationSeconds,
            _options.MaxAttemptDurationSeconds)));

        try
        {
            var session = await _workspaceProvider.PrepareAsync(snapshot, repository, timeout.Token);
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

            // Every attempt is a fresh conversation against one preserved workspace, so an earlier attempt's leftovers
            // are invisible unless the prompt says so; this one set feeds both the prompt and ValidateSubmission.
            var carriedFiles = await _patchEvidence.ListChangedPathsAsync(session, timeout.Token);
            var prompt = BuildPrompt(snapshot, session, profile, carriedFiles);
            await PersistPromptAsync(snapshot, prompt, session, repository, profile);
            var cloudContext = await CreateCloudContextAsync(snapshot, tools, timeout.Token);
            var model = await _coderModel.RunAsync(snapshot.ModelId,
                prompt,
                tools,
                maxOutputTokens,
                _options.MaxToolCalls,
                liveProgress,
                cloudContext?.Route,
                timeout.Token);
            var evidence = await _patchEvidence.ExportAsync(session, timeout.Token);
            liveProgress?.PatchObserved(evidence.ChangedFiles.Select(static item => item.Path).ToArray(),
                evidence.PatchBytes.LongLength,
                evidence.SubjectHash);
            ValidateSubmission(model.Submission, evidence, tools.CommandEvidence, carriedFiles);
            DevelopmentTestWritePolicy.Ensure(evidence, profile);
            await PersistEvidenceAsync(snapshot,
                model.Submission,
                evidence,
                tools.CommandEvidence,
                cloudContext?.ArtifactId,
                profile,
                timeout.Token);

            _ = await _store.TerminalizeAttemptAsync(new DevelopmentTerminalizeAttemptCommand
            {
                AttemptId = snapshot.AttemptId,
                OperationId = Guid.NewGuid(),
                Status = DevelopmentAttemptStatus.Succeeded,
                ExpectedAttemptVersion = snapshot.AttemptVersion,
                InputTokens = model.InputTokens,
                OutputTokens = model.OutputTokens
            },
                                CancellationToken.None);
            return new DevelopmentCoderAttemptResult
            {
                AttemptId = snapshot.AttemptId,
                BaseCommit = evidence.BaseCommit,
                SubjectHash = evidence.SubjectHash,
                PatchHash = evidence.PatchHash,
                ManifestHash = evidence.ManifestHash,
                ChangedFiles = evidence.ChangedFiles.Select(static item => item.Path).ToArray()
            };
        }
        catch (Exception exception)
        {
            var status = exception is OperationCanceledException ? DevelopmentAttemptStatus.Cancelled : DevelopmentAttemptStatus.Failed;
            try
            {
                _ = await _store.TerminalizeAttemptAsync(new DevelopmentTerminalizeAttemptCommand
                {
                    AttemptId = snapshot.AttemptId,
                    OperationId = Guid.NewGuid(),
                    Status = status,
                    ExpectedAttemptVersion = snapshot.AttemptVersion,
                    TerminalReason = SanitizedReason(exception)
                },
                                    CancellationToken.None);
            }
            catch (DevelopmentInvalidTransitionException)
            {
                // A concurrent terminal action already won; preserve the original coder failure.
            }

            throw;
        }
    }

    private async Task PersistEvidenceAsync(DevelopmentExecutionSnapshot snapshot,
        DevelopmentCoderSubmission submission,
        DevelopmentPatchEvidence evidence,
        IReadOnlyList<DevelopmentCommandEvidence> commands,
        Guid? cloudContextArtifactId,
        DevelopmentCommandProfile profile,
        CancellationToken cancellationToken)
    {
        var profileDigest = profile.ComputeDigest();
        IReadOnlyList<Guid>? cloudContextInputs = cloudContextArtifactId is { } contextArtifactId
            ? [contextArtifactId]
            : null;
        var patchId = await PersistArtifactAsync(snapshot,
            DevelopmentArtifactKind.Patch,
            evidence.PatchBytes,
            evidence,
            inputIds: cloudContextInputs,
            profileDigest,
            cancellationToken);
        var manifestId = await PersistArtifactAsync(snapshot,
            DevelopmentArtifactKind.ChangedFilesManifest,
            evidence.ManifestBytes,
            evidence,
            inputIds: cloudContextInputs,
            profileDigest,
            cancellationToken);
        var commandId = await PersistArtifactAsync(snapshot,
            DevelopmentArtifactKind.CommandResult,
            JsonSerializer.SerializeToUtf8Bytes(commands, JsonOptions),
            evidence,
            [patchId, manifestId],
            profileDigest,
            cancellationToken);
        var workspaceId = await PersistArtifactAsync(snapshot,
            DevelopmentArtifactKind.WorkspaceManifest,
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                evidence.BaseCommit,
                evidence.PatchHash,
                evidence.ManifestHash,
                evidence.SubjectHash,
                evidence.ExpectedResultHash,

                // The artifact protocol version, unchanged. The command profile that produced this workspace is
                // recorded separately below, because the two answer different questions.
                commandProfileVersion = DevelopmentWorkspaceTools.ProfileVersion,
                commandProfileId = profile.ProfileId,
                commandProfileDigest = profileDigest,
                changeIsolationOnly = true,
                osIsolationClaimed = false
            }, JsonOptions),
            evidence,
            [patchId, manifestId, commandId],
            profileDigest,
            cancellationToken);
        _ = await PersistArtifactAsync(snapshot,
            DevelopmentArtifactKind.CoderSubmission,
            JsonSerializer.SerializeToUtf8Bytes(submission, JsonOptions),
            evidence,
            [patchId, manifestId, commandId, workspaceId],
            profileDigest,
            cancellationToken);
    }

    /// <summary>
    ///     Records what this attempt told the model, before the model is called.
    /// </summary>
    /// <remarks>
    ///     The prompts an operator most needs belong to attempts that time out, exhaust the tool loop or lose their
    ///     evidence check, and none of those reach <see cref="PersistEvidenceAsync" />; the record carries a base
    ///     commit but no subject or manifest hash, which do not exist yet. Written under
    ///     <see cref="CancellationToken.None" /> so the attempt's own deadline cannot cancel the record of what
    ///     happened, and best-effort with a logged swallow so recording an observation is never a new way to fail.
    /// </remarks>
    private async Task PersistPromptAsync(DevelopmentExecutionSnapshot snapshot,
        string prompt,
        DevelopmentWorkspaceSession session,
        DevelopmentRepositoryBinding repository,
        DevelopmentCommandProfile profile)
    {
        try
        {
            var sanitized = DevelopmentArtifactSanitizer.SanitizePromptText(prompt,
                profile.ProtectedPaths,
                DevelopmentArtifactSanitizer.ResolveProtectedRoots(repository.RepositoryRoot, session));
            _ = await PersistArtifactAsync(snapshot,
                DevelopmentArtifactKind.Prompt,
                Encoding.UTF8.GetBytes(sanitized),
                session.BaseCommit,
                subjectHash: null,
                manifestHash: null,
                inputIds: null,
                profile.ComputeDigest(),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Development coder prompt for attempt {AttemptId} on task {TaskId} was not recorded.",
                snapshot.AttemptId,
                snapshot.TaskId);
        }
    }

    private Task<Guid> PersistArtifactAsync(DevelopmentExecutionSnapshot snapshot,
        DevelopmentArtifactKind kind,
        byte[] content,
        DevelopmentPatchEvidence evidence,
        IReadOnlyList<Guid>? inputIds,
        string profileDigest,
        CancellationToken cancellationToken) =>
        PersistArtifactAsync(snapshot,
            kind,
            content,
            evidence.BaseCommit,
            evidence.SubjectHash,
            evidence.ManifestHash,
            inputIds,
            profileDigest,
            cancellationToken);

    private async Task<Guid> PersistArtifactAsync(DevelopmentExecutionSnapshot snapshot,
        DevelopmentArtifactKind kind,
        byte[] content,
        string baseCommit,
        string? subjectHash,
        string? manifestHash,
        IReadOnlyList<Guid>? inputIds,
        string profileDigest,
        CancellationToken cancellationToken)
    {
        var artifactId = Guid.NewGuid();
        var written = await _blobStore.WriteAsync(snapshot.ProjectId, artifactId, content, cancellationToken);
        _ = await _store.AttachArtifactAsync(new DevelopmentAttachArtifactCommand
        {
            ArtifactId = artifactId,
            ProjectId = snapshot.ProjectId,
            TaskId = snapshot.TaskId,
            AttemptId = snapshot.AttemptId,
            OperationId = Guid.NewGuid(),
            Kind = kind,
            SchemaVersion = 1,
            ContentHash = written.ContentHash,
            ByteCount = written.ByteCount,
            ManagedReference = written.OpaqueReference,
            BaseCommit = baseCommit,
            SubjectHash = subjectHash,
            ChangedFilesManifestHash = manifestHash,
            InputArtifactIdsJson = inputIds is null ? null : JsonSerializer.SerializeToUtf8Bytes(inputIds, JsonOptions),
            CommandProfileVersion = DevelopmentWorkspaceTools.ProfileVersion,
            CommandProfileDigest = profileDigest
        },
                            cancellationToken);
        return artifactId;
    }

    private async Task<DevelopmentCloudAttemptContext?> CreateCloudContextAsync(DevelopmentExecutionSnapshot snapshot,
        IDevelopmentWorkspaceTools tools,
        CancellationToken cancellationToken)
    {
        if (string.Equals(snapshot.Provider, "local", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var files = await tools.ListFilesAsync(path: null, cancellationToken: cancellationToken);
        var currentDiff = await tools.GetDiffAsync(cancellationToken);
        return await _cloudContext.CreateAsync(snapshot,
            [
                new DevelopmentCloudContextExcerpt { RelativePath = "workspace-files.txt", Content = files },
                new DevelopmentCloudContextExcerpt { RelativePath = "workspace-diff.patch", Content = currentDiff }
            ],
            cancellationToken: cancellationToken);
    }

    private static void EnsureRunnable(DevelopmentExecutionSnapshot snapshot)
    {
        if (snapshot.AttemptRole != DevelopmentAttemptRole.Coder
            || snapshot.AttemptStatus != DevelopmentAttemptStatus.Running
            || snapshot.EgressPolicy is not (DevelopmentEgressPolicy.LocalOnly or DevelopmentEgressPolicy.CloudScoped)
            || string.IsNullOrWhiteSpace(snapshot.ModelId)
            || string.IsNullOrWhiteSpace(snapshot.Provider)
            || (snapshot.EgressPolicy == DevelopmentEgressPolicy.LocalOnly
                && !string.Equals(snapshot.Provider, "local", StringComparison.OrdinalIgnoreCase)))
        {
            throw new DevelopmentInvalidTransitionException("Only one running coder attempt with a valid egress policy and explicit model/provider can execute.");
        }
    }

    private static void ValidateSubmission(DevelopmentCoderSubmission submission,
        DevelopmentPatchEvidence evidence,
        IReadOnlyList<DevelopmentCommandEvidence> commands,
        IReadOnlySet<string> carriedFiles)
    {
        if (string.IsNullOrWhiteSpace(submission.Summary))
        {
            throw new DevelopmentAttemptEvidenceException(DevelopmentAttemptFailureCodes.MissingSummary,
                "The Development coder's submit_implementation call had an empty summary.");
        }

        var actualFiles = evidence.ChangedFiles.Select(static item => item.Path).ToHashSet(StringComparer.Ordinal);
        var submittedFiles = submission.ChangedFiles.Select(path => DevelopmentWorkspaceSecurity.Confine(path, allowRoot: false))
                                       .Where(static path => path.IsAccepted)
                                       .Select(static path => path.RelativePath)
                                       .ToHashSet(StringComparer.Ordinal);
        var missing = actualFiles.Except(submittedFiles, StringComparer.Ordinal).ToArray();

        // A carried path returned to its base content is reported honestly, not over-reported, and the manifest comes
        // from git rather than the submission. Under-reporting stays fatal: that is how a silent change escapes review.
        var overReported = submittedFiles.Except(actualFiles, StringComparer.Ordinal)
                                         .Where(path => !carriedFiles.Contains(path))
                                         .ToArray();
        if (missing.Length != 0 || overReported.Length != 0)
        {
            // The difference is named because the workspace is per task, not per attempt: without it an operator cannot
            // tell an under-reporting model from files an earlier failed attempt left in the preserved workspace.
            throw new DevelopmentAttemptEvidenceException(DevelopmentAttemptFailureCodes.ChangedFileManifestMismatch,
                "The Development coder's submitted changed-file list is not exactly the workspace's changed files. "
                + Describe("Changed but not submitted", missing)
                + Describe("Submitted but not changed", overReported)
                + "The workspace is shared by every attempt on this task, so files a previous attempt left behind also count as changed. "
                + "A path that differs from the base commit neither now nor at this attempt's start is over-reported.");
        }

        var executed = commands.Select(static command => command.CommandId).ToHashSet(StringComparer.Ordinal);
        var unexecuted = submission.CommandIds.Where(commandId => !executed.Contains(commandId)).ToArray();
        if (unexecuted.Length != 0)
        {
            throw new DevelopmentAttemptEvidenceException(DevelopmentAttemptFailureCodes.UnexecutedCommandClaimed,
                "The Development coder claimed command evidence it never produced. "
                + Describe("Claimed but not run", unexecuted));
        }
    }

    /// <summary>
    ///     Renders one side of a set difference, bounded by default to what the persisted terminal-reason column can
    ///     hold; the coder prompt is not that column and passes its own, larger bound.
    /// </summary>
    private static string Describe(string label, IEnumerable<string> paths, int limit = MaxDescribedPaths)
    {
        var bounded = paths.Order(StringComparer.Ordinal).ToArray();
        if (bounded.Length == 0)
        {
            return string.Empty;
        }

        var shown = string.Join(", ", bounded.Take(limit));
        var remainder = bounded.Length - limit;
        return remainder > 0
            ? $"{label}: {shown} (+{remainder} more). "
            : $"{label}: {shown}. ";
    }

    private const int MaxDescribedPaths = 5;

    /// <summary>The prompt's own bound on the carried-file list.</summary>
    /// <remarks>
    ///     Larger than <see cref="MaxDescribedPaths" />, which is sized for the 1024-character persisted terminal
    ///     reason, because a prompt the model has to act on is worth more paths. Nothing else bounds the count:
    ///     <c>MaxChangedFiles</c> is enforced by the export, not by the listing this renders.
    /// </remarks>
    private const int MaxPromptedCarriedPaths = 20;

    /// <summary>Internal so the composition can be pinned directly; nothing outside this class calls it.</summary>
    internal static string BuildPrompt(DevelopmentExecutionSnapshot snapshot,
        DevelopmentWorkspaceSession session,
        DevelopmentCommandProfile profile,
        IReadOnlySet<string> carriedFiles)
    {
        // The valid run_command ids are per-project now, so they are named here rather than in the tool's
        // [Description] attribute, which cannot interpolate them. The model still only ever sees a closed set.
        return string.Concat("Task: ", snapshot.Title,
            "\nRequirements:\n", snapshot.Requirements,
            "\nAcceptance criteria:\n", snapshot.AcceptanceCriteriaJson,
            "\n", DevelopmentTestWritePolicy.Prompt(profile),
            Policy(snapshot.WorkflowPolicyText),
            OperatorInstruction(snapshot.OperatorInstruction),
            Feedback(snapshot.PreviousRoundFeedback),
            "\nBase commit: ", session.BaseCommit,
            Carried(carriedFiles),
            "\nCommand profile: ", profile.ProfileId,
            "\nValid run_command ids: ", string.Join(", ", profile.Commands.Select(static command => command.CommandId)),
            "\nUse only the fixed tools. The worktree is detached change isolation, not an OS security boundary.",

            // The submission contract is stated because ValidateSubmission enforces it exactly: an unstated rule cost
            // a live attempt its correct fix, listed alone in changedFiles beside two incidental writes.
            "\n\nSubmission contract, enforced exactly:",
            "\n- Close the attempt with exactly one submit_implementation call, after all edits are done.",
            "\n- changedFiles must list every workspace file that differs from the base commit at submission time, and nothing else.",
            "\n  A file returned to its base-commit content is not one, even if an earlier attempt on this task changed it.",
            "\n  get_status lists those returned files too, so check it before submitting but do not copy it blindly.",
            "\n- commandIds must contain only ids you actually ran with run_command in this attempt.",
            "\n- summary must be non-empty.");
    }

    /// <summary>The files the shared workspace already carries into this attempt.</summary>
    /// <remarks>
    ///     A fresh conversation cannot know which files those are without spending tool calls, so a coder told the
    ///     rule but not the data reverts a carried file, reports it as changed and loses the attempt. The label
    ///     attributes nothing: a repository's own warm restore can leave un-ignored output here on a first attempt.
    /// </remarks>
    private static string Carried(IReadOnlySet<string> carriedFiles) =>
        carriedFiles.Count == 0
            ? string.Empty
            : string.Concat("\n",
                Describe("Files in this shared workspace that already differ from the base commit",
                    carriedFiles,
                    MaxPromptedCarriedPaths),
                "List them in changedFiles unless you return one to its base-commit content; a file you revert or delete back to the base commit is NOT a changed file.");

    /// <summary>
    ///     What a person told this task to do differently, and that it outranks everything else in the prompt.
    /// </summary>
    /// <remarks>
    ///     Under the previous round's heading the same sentence reads as one round's note, and a coder weighs the
    ///     task's own requirements higher. The requirements are immutable, so an operator who wrote them wrong has no
    ///     other way to correct them, and saying which one wins is the whole content of this section. The text is
    ///     bounded before it arrives — cut to the workflow's ceiling onto the node run's inputs — so this only
    ///     decides whether there is a section at all.
    /// </remarks>
    private static string OperatorInstruction(string? instruction) =>
        string.IsNullOrWhiteSpace(instruction)
            ? string.Empty
            : string.Concat("\nOperator instruction. This OUTRANKS the requirements, the acceptance criteria and any reviewer feedback below, wherever they conflict:\n",
                instruction,
                "\nDo what it says. Where it contradicts the requirements above, the operator has amended them: follow the operator, and say in your summary which requirement you are not meeting and why.",
                "\nIt does not amend the workspace test-write policy, which is enforced and cannot be waived.");

    /// <summary>What the last round was told to fix, when there was one.</summary>
    /// <remarks>
    ///     Without it a rework round is handed the same three fields the round before it was, and re-implements blind
    ///     — as true of an ordinary ChangesRequested round as of a workflow's routed one.
    /// </remarks>
    private static string Feedback(string? previousRound) =>
        string.IsNullOrWhiteSpace(previousRound)
            ? string.Empty
            : string.Concat("\nFeedback from the previous round:\n", previousRound);

    /// <summary>The rule sets a Development workflow resolved for the node run driving this task, when one does.</summary>
    /// <remarks>
    ///     Rendered and bounded by the workflow before it reached the task, so this only decides whether there is a
    ///     section at all: an empty heading governs nothing and would read as a policy that said nothing.
    /// </remarks>
    private static string Policy(string? workflowPolicy) =>
        string.IsNullOrWhiteSpace(workflowPolicy)
            ? string.Empty
            : string.Concat("\nPolicy (rule sets applied by the workflow):\n", workflowPolicy);

    /// <summary>The workspace policy's message, or the generic line when it cannot be shown safely.</summary>
    private static string PolicyReason(DevelopmentWorkspaceSecurityException exception)
    {
        try
        {
            return DevelopmentArtifactSanitizer.SanitizeText(exception.Message);
        }
        catch (DevelopmentWorkspaceSecurityException)
        {
            return "The Development coder attempt violated a workspace security policy.";
        }
    }

    private static string SanitizedReason(Exception exception)
    {
        return exception switch
        {
            OperationCanceledException => "The bounded Development coder attempt was cancelled or timed out.",

            // The policy's own sentence, because a generic stand-in leaves a node spending its whole retry budget on a
            // refusal nobody can name. Sanitized: an unauthored message interpolating a path falls back to the generic.
            DevelopmentWorkspaceSecurityException security => DevelopmentAttemptEvidenceException.Compose(DevelopmentAttemptFailureCodes.WorkspacePolicyRefused,
                PolicyReason(security)),

            // Authored here, never assembled from model output or an absolute host path, which is what makes it safe
            // to surface verbatim. Everything else still falls through to the generic reason.
            DevelopmentAttemptEvidenceException evidence => evidence.TerminalReason,
            _ => "The bounded Development coder attempt failed before producing valid exact evidence."
        };
    }
}
