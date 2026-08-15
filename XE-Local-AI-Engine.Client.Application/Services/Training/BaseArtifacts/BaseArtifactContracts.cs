namespace XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;

/// <summary>One file of a downloaded base checkpoint, as recorded in the encrypted <c>files_json</c> manifest.</summary>
public sealed record BaseArtifactFileView(string Role, string FileName, string LocalPath, long SizeBytes, string? Sha256);

/// <summary>
///     The licensing facts fetched for the base checkpoint repository — never for a GGUF quant repo derived from it
///     (locked decision 8). A <see langword="null" /> <paramref name="License" /> is itself the answer the license gate
///     presents: the repo declares no license tag.
/// </summary>
public sealed record BaseArtifactLicenseView(string RepoId, string? License, bool IsGated, DateTimeOffset FetchedAtUtc);

/// <summary>Live transfer progress for an in-flight download. Absent once the download reaches a terminal state.</summary>
public sealed record BaseArtifactProgressView(long CompletedBytes, long? TotalBytes, int FileIndex, int FileCount);

public sealed record BaseArtifactView(
    Guid Id,
    string RepoId,
    string Revision,
    string Status,
    long TotalBytes,
    IReadOnlyList<BaseArtifactFileView> Files,
    BaseArtifactLicenseView? License,
    string? ErrorMessage,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    BaseArtifactProgressView? Progress);

/// <summary>Why a delete did not happen.</summary>
public enum BaseArtifactDeleteOutcome
{
    Deleted = 0,
    NotFound = 1,

    /// <summary>The download is still running. Cancel it first — deleting under it would race the writer.</summary>
    Downloading = 2
}

/// <summary>
///     Acquisition and lifecycle of trainable base checkpoints: resolve the user-selected repository, preflight disk,
///     download its file set, and record the licensing metadata the run wizard's confirmation step reads.
/// </summary>
/// <remarks>
///     The repository is always operator-selected. Nothing here infers a base repo from an installed GGUF — models with
///     no resolvable base checkpoint are simply ineligible for training (locked decision 18), and guessing would produce
///     a run that trains for hours against the wrong weights.
/// </remarks>
public interface IBaseArtifactService
{
    /// <summary>
    ///     Resolves <paramref name="repoId" />, preflights disk, records the artifact, and starts the download in the
    ///     background. Returns immediately with the artifact in its <c>Downloading</c> state.
    /// </summary>
    Task<BaseArtifactView> StartDownloadAsync(string repoId, string? revision, CancellationToken ct);

    Task<IReadOnlyList<BaseArtifactView>> ListAsync(CancellationToken ct);

    Task<BaseArtifactView?> GetAsync(Guid artifactId, CancellationToken ct);

    /// <summary>The recorded license metadata, or <see langword="null" /> when the artifact does not exist.</summary>
    Task<BaseArtifactLicenseView?> GetLicenseAsync(Guid artifactId, CancellationToken ct);

    /// <summary>Requests cancellation of an in-flight download. False when nothing is running for that artifact.</summary>
    bool Cancel(Guid artifactId);

    Task<BaseArtifactDeleteOutcome> DeleteAsync(Guid artifactId, CancellationToken ct);
}

/// <summary>The selected repository or revision cannot be used. Message is operator-facing.</summary>
public sealed class BaseArtifactRejectedException : Exception
{
    public BaseArtifactRejectedException()
    {
    }

    public BaseArtifactRejectedException(string message)
        : base(message)
    {
    }

    public BaseArtifactRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
