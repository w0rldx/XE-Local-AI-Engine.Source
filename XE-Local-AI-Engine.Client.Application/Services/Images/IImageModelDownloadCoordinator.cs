namespace XE_Local_AI_Engine.Client.Services.Images;

using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     Coordinates operator-driven image-model weight downloads. Mirrors <c>IGgufDownloadCoordinator</c>: the download
///     runs detached on the application lifetime, and the coordinator keeps the latest sanitized
///     <see cref="ImageModelDownloadStatus" /> per model name so the operator UI can poll it.
///     <para>
///         <b>Why this exists.</b> The start endpoint previously fired
///         <see cref="IImageModelStore.EnsureModelAsync" /> on a detached task that swallowed every failure into a log
///         line. A mistyped weight file therefore produced a 202 + "download started" toast and then nothing, forever —
///         the operator could not distinguish "still fetching 4 GB" from "failed ten minutes ago". Every download now
///         ends in an observable terminal phase.
///     </para>
/// </summary>
public interface IImageModelDownloadCoordinator
{
    /// <summary>
    ///     Begins (or rejoins) a background download of <paramref name="request" />'s file-set. Returns once the download
    ///     is registered; the transfer itself outlives the call. A download already in flight for the same model name is
    ///     rejoined (idempotent) rather than duplicated, so a double-submit cannot start two transfers.
    /// </summary>
    ImageModelDownloadTicket Start(ImageModelRequest request);

    /// <summary>Returns the latest sanitized status for <paramref name="modelName" />, or <see langword="null" /> when unknown.</summary>
    ImageModelDownloadStatus? GetStatus(string modelName);

    /// <summary>Returns a snapshot of all tracked download statuses (in-flight and recently finished).</summary>
    IReadOnlyList<ImageModelDownloadStatus> ListStatuses();
}

/// <summary>The accepted-download identity returned by <see cref="IImageModelDownloadCoordinator.Start" />.</summary>
/// <param name="ModelName">The canonical model name the download is keyed by (poll its status by this).</param>
/// <param name="AlreadyInFlight"><c>true</c> when an existing download for the same model name was rejoined instead of started.</param>
public sealed record ImageModelDownloadTicket(string ModelName, bool AlreadyInFlight);

/// <summary>Phase of a coordinated image-model download.</summary>
public enum ImageModelDownloadPhase
{
    /// <summary>The download is running (bytes flowing or verifying).</summary>
    Running = 0,

    /// <summary>Every part downloaded and the file-set registered.</summary>
    Completed = 1,

    /// <summary>The download was cancelled cooperatively before completing.</summary>
    Cancelled = 2,

    /// <summary>
    ///     The download failed; <see cref="ImageModelDownloadStatus.SanitizedError" /> carries an operator-safe reason.
    /// </summary>
    Failed = 3
}

/// <summary>
///     A sanitized snapshot of one coordinated image-model download. Carries only the model name, phase, byte counts and
///     a sanitized reason — never an absolute path, URL, or token.
/// </summary>
public sealed record ImageModelDownloadStatus(
    string ModelName,
    ImageModelDownloadPhase Phase,
    long? CompletedBytes,
    long? TotalBytes,
    string? SanitizedError);
