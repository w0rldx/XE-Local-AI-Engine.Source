namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal interface IDevelopmentEvidenceService
{
    Task<DevelopmentEvidenceSet> ResolveCurrentAsync(Guid taskId,
        DevelopmentWorkspaceSession session,
        CancellationToken cancellationToken = default);

    Task<DevelopmentArtifactWith<ReadOnlyMemory<byte>>> ReadLatestAsync(Guid taskId,
        DevelopmentArtifactKind kind,
        CancellationToken cancellationToken = default);

    Task InvalidateApprovalEvidenceAsync(Guid taskId,
        string sanitizedReason,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Writes an artifact and stamps it with two independent dimensions.
    ///     <para>
    ///         <paramref name="commandProfileVersion" /> is the artifact <em>protocol</em> version
    ///         (<c>development-workspace-v1</c>, <c>development-validation-v2</c>, <c>development-review-v1</c>). It
    ///         describes the shape of the artifact and is what the apply and reviewer compatibility gates compare.
    ///     </para>
    ///     <para>
    ///         <paramref name="commandProfileDigest" /> is the SHA-256 of the canonical command profile that actually
    ///         produced the evidence. It describes which commands ran. These are not the same thing and must not be
    ///         collapsed into one field: replacing the protocol version with the digest would delete a compatibility
    ///         safeguard and replace it with something that does not validate artifact shape at all.
    ///     </para>
    /// </summary>
    Task<DevelopmentPreparedArtifact> PrepareAsync(DevelopmentExecutionSnapshot snapshot,
        DevelopmentArtifactKind kind,
        ReadOnlyMemory<byte> content,
        DevelopmentPatchEvidence evidence,
        IReadOnlyList<Guid> inputArtifactIds,
        string commandProfileVersion,
        string commandProfileDigest,
        CancellationToken cancellationToken = default);
}

internal sealed class DevelopmentEvidenceService(
    IDevelopmentStore store,
    IDevelopmentArtifactBlobStore blobStore,
    IDevelopmentPatchEvidenceService patchEvidence) : IDevelopmentEvidenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDevelopmentArtifactBlobStore _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
    private readonly IDevelopmentPatchEvidenceService _patchEvidence = patchEvidence ?? throw new ArgumentNullException(nameof(patchEvidence));
    private readonly IDevelopmentStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<DevelopmentEvidenceSet> ResolveCurrentAsync(Guid taskId,
        DevelopmentWorkspaceSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            var artifacts = await _store.ListArtifactsAsync(taskId, cancellationToken).ConfigureAwait(false);
            var patch = LatestValid(artifacts, DevelopmentArtifactKind.Patch);
            var manifest = LatestValid(artifacts, DevelopmentArtifactKind.ChangedFilesManifest);
            if (patch.AttemptId != manifest.AttemptId
                || !string.Equals(patch.BaseCommit, manifest.BaseCommit, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(patch.SubjectHash, manifest.SubjectHash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(patch.ChangedFilesManifestHash, manifest.ChangedFilesManifestHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new DevelopmentInvalidTransitionException("The latest patch and changed-file manifest are not one exact evidence subject.");
            }

            var patchContent = await ReadRequiredAsync(patch, cancellationToken).ConfigureAwait(false);
            var manifestContent = await ReadRequiredAsync(manifest, cancellationToken).ConfigureAwait(false);
            var current = await _patchEvidence.ExportAsync(session, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current.BaseCommit, patch.BaseCommit, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.PatchHash, patch.ContentHash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.ManifestHash, manifest.ContentHash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.ManifestHash, patch.ChangedFilesManifestHash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.SubjectHash, patch.SubjectHash, StringComparison.OrdinalIgnoreCase)
                || !patchContent.Span.SequenceEqual(current.PatchBytes)
                || !manifestContent.Span.SequenceEqual(current.ManifestBytes))
            {
                throw new DevelopmentInvalidTransitionException("The current workspace no longer matches its exact patch evidence.");
            }

            return new DevelopmentEvidenceSet(current, patch, manifest, patchContent, manifestContent);
        }
        catch (DevelopmentInvalidTransitionException)
        {
            await InvalidateApprovalEvidenceAsync(taskId,
                "The exact Development evidence no longer matches the current workspace.",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<DevelopmentArtifactWith<ReadOnlyMemory<byte>>> ReadLatestAsync(Guid taskId,
        DevelopmentArtifactKind kind,
        CancellationToken cancellationToken = default)
    {
        var artifacts = await _store.ListArtifactsAsync(taskId, cancellationToken).ConfigureAwait(false);
        var artifact = LatestValid(artifacts, kind);
        return new DevelopmentArtifactWith<ReadOnlyMemory<byte>>(artifact, await ReadRequiredAsync(artifact, cancellationToken).ConfigureAwait(false));
    }

    public async Task InvalidateApprovalEvidenceAsync(Guid taskId,
        string sanitizedReason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedReason);
        var task = await _store.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task.Status is not (DevelopmentTaskStatus.Validation
            or DevelopmentTaskStatus.InReview
            or DevelopmentTaskStatus.AwaitingApply))
        {
            return;
        }

        try
        {
            _ = await _store.InvalidateEvidenceAsync(new DevelopmentInvalidateEvidenceCommand(taskId,
                                    Guid.NewGuid(),
                                    task.Version,
                                    sanitizedReason),
                                cancellationToken)
                            .ConfigureAwait(false);
        }
        catch (DevelopmentConcurrencyException)
        {
            var current = await _store.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (current.Status != DevelopmentTaskStatus.InProgress)
            {
                throw;
            }
        }
        catch (DevelopmentInvalidTransitionException)
        {
            var current = await _store.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (current.Status != DevelopmentTaskStatus.InProgress)
            {
                throw;
            }
        }
    }

    public async Task<DevelopmentPreparedArtifact> PrepareAsync(DevelopmentExecutionSnapshot snapshot,
        DevelopmentArtifactKind kind,
        ReadOnlyMemory<byte> content,
        DevelopmentPatchEvidence evidence,
        IReadOnlyList<Guid> inputArtifactIds,
        string commandProfileVersion,
        string commandProfileDigest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(inputArtifactIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandProfileVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandProfileDigest);
        if (kind is DevelopmentArtifactKind.ValidationReport or DevelopmentArtifactKind.ReviewReport)
        {
            _ = DevelopmentArtifactSanitizer.SanitizeText(Encoding.UTF8.GetString(content.Span));
        }

        var artifactId = Guid.NewGuid();
        var written = await _blobStore.WriteAsync(snapshot.ProjectId, artifactId, content, cancellationToken).ConfigureAwait(false);
        return new DevelopmentPreparedArtifact(artifactId,
            new DevelopmentAttachArtifactCommand(artifactId,
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
                InputArtifactIdsJson: JsonSerializer.SerializeToUtf8Bytes(inputArtifactIds, JsonOptions),
                CommandProfileVersion: commandProfileVersion,
                CommandProfileDigest: commandProfileDigest));
    }

    private async Task<ReadOnlyMemory<byte>> ReadRequiredAsync(DevelopmentArtifactSnapshot artifact, CancellationToken cancellationToken)
    {
        if (artifact.ManagedReference is null)
        {
            throw new DevelopmentInvalidTransitionException($"The {artifact.Kind} artifact is not backed by a managed immutable blob.");
        }

        var read = await _blobStore.ReadAsync(artifact.ProjectId,
            artifact.Id,
            artifact.ContentHash,
            artifact.ByteCount,
            cancellationToken).ConfigureAwait(false);
        if (read.Status != DevelopmentArtifactReadStatus.Found)
        {
            throw new DevelopmentInvalidTransitionException($"The {artifact.Kind} artifact failed immutable blob verification ({read.Status}).");
        }

        return read.Content;
    }

    private static DevelopmentArtifactSnapshot LatestValid(IReadOnlyList<DevelopmentArtifactSnapshot> artifacts,
        DevelopmentArtifactKind kind) =>
        artifacts.LastOrDefault(artifact => artifact.Kind == kind && artifact.IsValid)
        ?? throw new DevelopmentInvalidTransitionException($"No valid {kind} artifact exists for the Development task.");
}
