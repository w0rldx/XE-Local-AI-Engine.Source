namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Sandbox;

internal sealed record DevelopmentCoderAttemptResult(
    Guid AttemptId,
    string BaseCommit,
    string SubjectHash,
    string PatchHash,
    string ManifestHash,
    IReadOnlyList<string> ChangedFiles);

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
        IDevelopmentAttemptLiveBroker? liveBroker = null,
        TimeProvider? timeProvider = null)
    {
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
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DevelopmentCoderAttemptResult> RunAsync(Guid attemptId,
        DevelopmentRepositoryBinding repository,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _store.GetExecutionSnapshotAsync(attemptId, cancellationToken).ConfigureAwait(false);
        EnsureRunnable(snapshot);
        var profile = DevelopmentCommandProfileCatalog.ResolveStored(snapshot.CommandProfileJson);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(snapshot.MaxDurationSeconds ?? _options.MaxAttemptDurationSeconds,
            _options.MaxAttemptDurationSeconds)));

        try
        {
            var session = await _workspaceProvider.PrepareAsync(snapshot, repository, timeout.Token).ConfigureAwait(false);
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

            // Every attempt on a task is a fresh conversation against ONE preserved workspace, so what an earlier
            // attempt left behind is invisible to this one unless the prompt says so. Read before the model runs, and
            // the same set decides both what the coder is told and what ValidateSubmission will forgive.
            var carriedFiles = await _patchEvidence.ListChangedPathsAsync(session, timeout.Token).ConfigureAwait(false);
            var prompt = BuildPrompt(snapshot, session, profile, carriedFiles);
            var cloudContext = await CreateCloudContextAsync(snapshot, tools, timeout.Token).ConfigureAwait(false);
            var model = await _coderModel.RunAsync(snapshot.ModelId,
                prompt,
                tools,
                maxOutputTokens,
                _options.MaxToolCalls,
                liveProgress,
                cloudContext?.Route,
                timeout.Token).ConfigureAwait(false);
            var evidence = await _patchEvidence.ExportAsync(session, timeout.Token).ConfigureAwait(false);
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
                timeout.Token).ConfigureAwait(false);

            _ = await _store.TerminalizeAttemptAsync(new DevelopmentTerminalizeAttemptCommand(snapshot.AttemptId,
                                    Guid.NewGuid(),
                                    DevelopmentAttemptStatus.Succeeded,
                                    snapshot.AttemptVersion,
                                    InputTokens: model.InputTokens,
                                    OutputTokens: model.OutputTokens),
                                CancellationToken.None)
                            .ConfigureAwait(false);
            return new DevelopmentCoderAttemptResult(snapshot.AttemptId,
                evidence.BaseCommit,
                evidence.SubjectHash,
                evidence.PatchHash,
                evidence.ManifestHash,
                evidence.ChangedFiles.Select(static item => item.Path).ToArray());
        }
        catch (Exception exception)
        {
            var status = exception is OperationCanceledException ? DevelopmentAttemptStatus.Cancelled : DevelopmentAttemptStatus.Failed;
            try
            {
                _ = await _store.TerminalizeAttemptAsync(new DevelopmentTerminalizeAttemptCommand(snapshot.AttemptId,
                                        Guid.NewGuid(),
                                        status,
                                        snapshot.AttemptVersion,
                                        SanitizedReason(exception)),
                                    CancellationToken.None)
                                .ConfigureAwait(false);
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
            cancellationToken).ConfigureAwait(false);
        var manifestId = await PersistArtifactAsync(snapshot,
            DevelopmentArtifactKind.ChangedFilesManifest,
            evidence.ManifestBytes,
            evidence,
            inputIds: cloudContextInputs,
            profileDigest,
            cancellationToken).ConfigureAwait(false);
        var commandId = await PersistArtifactAsync(snapshot,
            DevelopmentArtifactKind.CommandResult,
            JsonSerializer.SerializeToUtf8Bytes(commands, JsonOptions),
            evidence,
            [patchId, manifestId],
            profileDigest,
            cancellationToken).ConfigureAwait(false);
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
            cancellationToken).ConfigureAwait(false);
        _ = await PersistArtifactAsync(snapshot,
            DevelopmentArtifactKind.CoderSubmission,
            JsonSerializer.SerializeToUtf8Bytes(submission, JsonOptions),
            evidence,
            [patchId, manifestId, commandId, workspaceId],
            profileDigest,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid> PersistArtifactAsync(DevelopmentExecutionSnapshot snapshot,
        DevelopmentArtifactKind kind,
        byte[] content,
        DevelopmentPatchEvidence evidence,
        IReadOnlyList<Guid>? inputIds,
        string profileDigest,
        CancellationToken cancellationToken)
    {
        var artifactId = Guid.NewGuid();
        var written = await _blobStore.WriteAsync(snapshot.ProjectId, artifactId, content, cancellationToken).ConfigureAwait(false);
        _ = await _store.AttachArtifactAsync(new DevelopmentAttachArtifactCommand(artifactId,
                                snapshot.ProjectId,
                                snapshot.TaskId,
                                snapshot.AttemptId,
                                Guid.NewGuid(),
                                kind,
                                SchemaVersion: 1,
                                written.ContentHash,
                                written.ByteCount,
                                ManagedReference: written.OpaqueReference,
                                BaseCommit: evidence.BaseCommit,
                                SubjectHash: evidence.SubjectHash,
                                ChangedFilesManifestHash: evidence.ManifestHash,
                                InputArtifactIdsJson: inputIds is null ? null : JsonSerializer.SerializeToUtf8Bytes(inputIds, JsonOptions),
                                CommandProfileVersion: DevelopmentWorkspaceTools.ProfileVersion,
                                CommandProfileDigest: profileDigest),
                            cancellationToken)
                        .ConfigureAwait(false);
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

        var files = await tools.ListFilesAsync(path: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        var currentDiff = await tools.GetDiffAsync(cancellationToken).ConfigureAwait(false);
        return await _cloudContext.CreateAsync(snapshot,
            [
                new DevelopmentCloudContextExcerpt("workspace-files.txt", files),
                new DevelopmentCloudContextExcerpt("workspace-diff.patch", currentDiff)
            ],
            cancellationToken: cancellationToken).ConfigureAwait(false);
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

        // A path that no longer differs from the base commit but did when this attempt started was changed by an
        // earlier attempt on this shared workspace and returned to base by this one. The coder read it in its prompt
        // and is reporting the file it touched, which is honest, not over-reporting — and the persisted manifest is
        // derived from git rather than from the submission, so accepting the claim cannot corrupt the evidence.
        // Under-reporting stays fatal: that is the direction in which a silent change escapes review.
        var overReported = submittedFiles.Except(actualFiles, StringComparer.Ordinal)
                                         .Where(path => !carriedFiles.Contains(path))
                                         .ToArray();
        if (missing.Length != 0 || overReported.Length != 0)
        {
            // Naming the difference is the whole point. The generic message this replaced left the operator unable to
            // tell "the model under-reported" from "an earlier failed attempt left files in this task's preserved
            // workspace" — which is a real and common cause, because the workspace is per task, not per attempt.
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

    /// <summary>
    ///     The prompt's own bound on the carried-file list. Larger than <see cref="MaxDescribedPaths" /> because that
    ///     one is sized for the 1024-character persisted terminal reason, and a prompt the model has to act on is worth
    ///     more paths. Nothing else bounds the count: <c>MaxChangedFiles</c> is enforced by the export, not by the
    ///     listing this renders.
    /// </summary>
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
            Policy(snapshot.WorkflowPolicyText),
            Feedback(snapshot.PreviousRoundFeedback),
            "\nBase commit: ", session.BaseCommit,
            Carried(carriedFiles),
            "\nCommand profile: ", profile.ProfileId,
            "\nValid run_command ids: ", string.Join(", ", profile.Commands.Select(static command => command.CommandId)),
            "\nUse only the fixed tools. The worktree is detached change isolation, not an OS security boundary.",

            // The submission contract is stated here because ValidateSubmission enforces it exactly, and until now
            // nothing told the model what it was. Measured live on 2026-07-31: a capable model produced a correct fix,
            // also wrote two incidental files, listed only the fix in changedFiles, and the whole attempt — including
            // the correct fix — was discarded for a rule it had never been given.
            "\n\nSubmission contract, enforced exactly:",
            "\n- Close the attempt with exactly one submit_implementation call, after all edits are done.",
            "\n- changedFiles must list every workspace file that differs from the base commit at submission time, and nothing else.",
            "\n  A file returned to its base-commit content is not one, even if an earlier attempt on this task changed it.",
            "\n  get_status lists those returned files too, so check it before submitting but do not copy it blindly.",
            "\n- commandIds must contain only ids you actually ran with run_command in this attempt.",
            "\n- summary must be non-empty.");
    }

    /// <summary>
    ///     The files the shared workspace already carries into this attempt. Live on 2026-09-04 a coder reverted a file
    ///     an earlier attempt had created, reported it as changed, and lost the whole attempt: it was told the rule but
    ///     never the data, and a fresh conversation cannot know which files those are without spending tool calls to
    ///     find out. The label attributes nothing, because a repository's own warm restore can leave un-ignored output
    ///     here on a task's very first attempt.
    /// </summary>
    private static string Carried(IReadOnlySet<string> carriedFiles) =>
        carriedFiles.Count == 0
            ? string.Empty
            : string.Concat("\n",
                Describe("Files in this shared workspace that already differ from the base commit",
                    carriedFiles,
                    MaxPromptedCarriedPaths),
                "List them in changedFiles unless you return one to its base-commit content; a file you revert or delete back to the base commit is NOT a changed file.");

    /// <summary>
    ///     What the last round was told to fix, when there was one. Without this a rework round is handed the SAME
    ///     three fields the round before it was handed, and re-implements blind — which is true of an ordinary Dev Mode
    ///     ChangesRequested round as much as of a workflow's routed one.
    /// </summary>
    private static string Feedback(string? previousRound) =>
        string.IsNullOrWhiteSpace(previousRound)
            ? string.Empty
            : string.Concat("\nFeedback from the previous round:\n", previousRound);

    /// <summary>
    ///     The rule sets a Development workflow resolved for the node run driving this task, when one does. Rendered and
    ///     bounded by the workflow before it ever reached the task, so this only decides whether there is a section at
    ///     all: an empty heading governs nothing and would read as a policy that said nothing.
    /// </summary>
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

            // The POLICY's own sentence, not a generic stand-in for it: "violated a workspace security policy" tells an
            // operator nothing to change, and a workflow node that spends its whole retry budget on a test-write
            // refusal spends it without anyone ever being told which rule it broke. Sanitized because not every
            // workspace-security message is authored — some interpolate a path — and a message the sanitizer refuses
            // falls back to the generic line rather than escaping.
            DevelopmentWorkspaceSecurityException security => DevelopmentAttemptEvidenceException.Compose(DevelopmentAttemptFailureCodes.WorkspacePolicyRefused,
                PolicyReason(security)),

            // Authored here, never assembled from model output or an absolute host path, which is what makes it safe
            // to surface verbatim. Everything else still falls through to the generic reason.
            DevelopmentAttemptEvidenceException evidence => evidence.TerminalReason,
            _ => "The bounded Development coder attempt failed before producing valid exact evidence."
        };
    }
}
