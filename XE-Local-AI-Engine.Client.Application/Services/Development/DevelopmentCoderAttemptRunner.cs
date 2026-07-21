namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text;
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
        string repositoryRoot,
        CancellationToken cancellationToken = default);
}

internal sealed class DevelopmentCoderAttemptRunner : IDevelopmentCoderAttemptRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDevelopmentArtifactBlobStore _blobStore;
    private readonly IDevelopmentCoderModel _coderModel;
    private readonly DevelopmentOptions _options;
    private readonly IDevelopmentPatchEvidenceService _patchEvidence;
    private readonly ISandboxRuntimeProvider _sandbox;
    private readonly IDevelopmentStore _store;
    private readonly IDevelopmentWorkspaceProvider _workspaceProvider;

    public DevelopmentCoderAttemptRunner(IDevelopmentStore store,
        IDevelopmentWorkspaceProvider workspaceProvider,
        ISandboxRuntimeProvider sandbox,
        IDevelopmentPatchEvidenceService patchEvidence,
        IDevelopmentArtifactBlobStore blobStore,
        IDevelopmentCoderModel coderModel,
        IOptions<DevelopmentOptions> options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workspaceProvider = workspaceProvider ?? throw new ArgumentNullException(nameof(workspaceProvider));
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        _patchEvidence = patchEvidence ?? throw new ArgumentNullException(nameof(patchEvidence));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        _coderModel = coderModel ?? throw new ArgumentNullException(nameof(coderModel));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public async Task<DevelopmentCoderAttemptResult> RunAsync(Guid attemptId,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _store.GetExecutionSnapshotAsync(attemptId, cancellationToken).ConfigureAwait(false);
        EnsureRunnable(snapshot);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(snapshot.MaxDurationSeconds ?? _options.MaxAttemptDurationSeconds,
            _options.MaxAttemptDurationSeconds)));

        try
        {
            var session = await _workspaceProvider.PrepareAsync(snapshot, repositoryRoot, timeout.Token).ConfigureAwait(false);
            var tools = new DevelopmentWorkspaceTools(_sandbox, session, Options.Create(_options));
            var prompt = BuildPrompt(snapshot, session);
            var model = await _coderModel.RunAsync(snapshot.ModelId,
                prompt,
                tools,
                Math.Min(snapshot.MaxTokens ?? _options.MaxOutputTokens, _options.MaxOutputTokens),
                _options.MaxToolCalls,
                timeout.Token).ConfigureAwait(false);
            var evidence = await _patchEvidence.ExportAsync(session, timeout.Token).ConfigureAwait(false);
            ValidateSubmission(model.Submission, evidence, tools.CommandEvidence);
            await PersistEvidenceAsync(snapshot, model.Submission, evidence, tools.CommandEvidence, timeout.Token).ConfigureAwait(false);

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
        CancellationToken cancellationToken)
    {
        var patchId = await PersistArtifactAsync(snapshot,
            DevelopmentArtifactKind.Patch,
            evidence.PatchBytes,
            evidence,
            inputIds: null,
            cancellationToken).ConfigureAwait(false);
        var manifestId = await PersistArtifactAsync(snapshot,
            DevelopmentArtifactKind.ChangedFilesManifest,
            evidence.ManifestBytes,
            evidence,
            inputIds: null,
            cancellationToken).ConfigureAwait(false);
        var commandId = await PersistArtifactAsync(snapshot,
            DevelopmentArtifactKind.CommandResult,
            JsonSerializer.SerializeToUtf8Bytes(commands, JsonOptions),
            evidence,
            [patchId, manifestId],
            cancellationToken).ConfigureAwait(false);
        _ = await PersistArtifactAsync(snapshot,
            DevelopmentArtifactKind.WorkspaceManifest,
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                submission,
                evidence.BaseCommit,
                evidence.PatchHash,
                evidence.ManifestHash,
                evidence.SubjectHash,
                commandProfileVersion = DevelopmentWorkspaceTools.ProfileVersion,
                changeIsolationOnly = true,
                osIsolationClaimed = false
            }, JsonOptions),
            evidence,
            [patchId, manifestId, commandId],
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

    private static void EnsureRunnable(DevelopmentExecutionSnapshot snapshot)
    {
        if (snapshot.AttemptRole != DevelopmentAttemptRole.Coder
            || snapshot.AttemptStatus != DevelopmentAttemptStatus.Running
            || snapshot.EgressPolicy != DevelopmentEgressPolicy.LocalOnly
            || string.IsNullOrWhiteSpace(snapshot.ModelId))
        {
            throw new DevelopmentInvalidTransitionException("Only one running LocalOnly coder attempt with an explicit model id can enter Gate 3.");
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
