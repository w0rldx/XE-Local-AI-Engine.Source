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
    private readonly ISandboxRuntimeProvider _sandbox;
    private readonly IDevelopmentStore _store;
    private readonly IDevelopmentWorkspaceProvider _workspaceProvider;
    private readonly TimeProvider _timeProvider;

    public DevelopmentCoderAttemptRunner(IDevelopmentStore store,
        IDevelopmentWorkspaceProvider workspaceProvider,
        ISandboxRuntimeProvider sandbox,
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
            var tools = new DevelopmentWorkspaceTools(_sandbox, session, Options.Create(_options), liveProgress);
            var prompt = BuildPrompt(snapshot, session);
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
            ValidateSubmission(model.Submission, evidence, tools.CommandEvidence);
            await PersistEvidenceAsync(snapshot,
                model.Submission,
                evidence,
                tools.CommandEvidence,
                cloudContext?.ArtifactId,
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
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid>? cloudContextInputs = cloudContextArtifactId is { } contextArtifactId
            ? [contextArtifactId]
            : null;
        var patchId = await PersistArtifactAsync(snapshot,
            DevelopmentArtifactKind.Patch,
            evidence.PatchBytes,
            evidence,
            inputIds: cloudContextInputs,
            cancellationToken).ConfigureAwait(false);
        var manifestId = await PersistArtifactAsync(snapshot,
            DevelopmentArtifactKind.ChangedFilesManifest,
            evidence.ManifestBytes,
            evidence,
            inputIds: cloudContextInputs,
            cancellationToken).ConfigureAwait(false);
        var commandId = await PersistArtifactAsync(snapshot,
            DevelopmentArtifactKind.CommandResult,
            JsonSerializer.SerializeToUtf8Bytes(commands, JsonOptions),
            evidence,
            [patchId, manifestId],
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
                commandProfileVersion = DevelopmentWorkspaceTools.ProfileVersion,
                changeIsolationOnly = true,
                osIsolationClaimed = false
            }, JsonOptions),
            evidence,
            [patchId, manifestId, commandId],
            cancellationToken).ConfigureAwait(false);
        _ = await PersistArtifactAsync(snapshot,
            DevelopmentArtifactKind.CoderSubmission,
            JsonSerializer.SerializeToUtf8Bytes(submission, JsonOptions),
            evidence,
            [patchId, manifestId, commandId, workspaceId],
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid> PersistArtifactAsync(DevelopmentExecutionSnapshot snapshot,
        DevelopmentArtifactKind kind,
        byte[] content,
        DevelopmentPatchEvidence evidence,
        IReadOnlyList<Guid>? inputIds,
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
                                CommandProfileVersion: DevelopmentWorkspaceTools.ProfileVersion),
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
        IReadOnlyList<DevelopmentCommandEvidence> commands)
    {
        if (string.IsNullOrWhiteSpace(submission.Summary))
        {
            throw new ArgumentException("The typed coder submission requires a summary.", nameof(submission));
        }

        var actualFiles = evidence.ChangedFiles.Select(static item => item.Path).ToHashSet(StringComparer.Ordinal);
        var submittedFiles = submission.ChangedFiles.Select(path => DevelopmentWorkspaceSecurity.Confine(path, allowRoot: false))
                                       .Where(static path => path.IsAccepted)
                                       .Select(static path => path.RelativePath)
                                       .ToHashSet(StringComparer.Ordinal);
        if (!actualFiles.SetEquals(submittedFiles))
        {
            throw new InvalidOperationException("The typed coder submission does not match the exact changed-file manifest.");
        }

        var executed = commands.Select(static command => command.CommandId).ToHashSet(StringComparer.Ordinal);
        if (submission.CommandIds.Any(commandId => !executed.Contains(commandId)))
        {
            throw new InvalidOperationException("The typed coder submission claims command evidence that was not executed.");
        }
    }

    private static string BuildPrompt(DevelopmentExecutionSnapshot snapshot, DevelopmentWorkspaceSession session)
    {
        return string.Concat("Task: ", snapshot.Title,
            "\nRequirements:\n", snapshot.Requirements,
            "\nAcceptance criteria:\n", snapshot.AcceptanceCriteriaJson,
            "\nBase commit: ", session.BaseCommit,
            "\nUse only the fixed tools. The worktree is detached change isolation, not an OS security boundary.");
    }

    private static string SanitizedReason(Exception exception)
    {
        return exception switch
        {
            OperationCanceledException => "The bounded Development coder attempt was cancelled or timed out.",
            DevelopmentWorkspaceSecurityException => "The Development coder attempt violated a workspace security policy.",
            _ => "The bounded Development coder attempt failed before producing valid exact evidence."
        };
    }
}
