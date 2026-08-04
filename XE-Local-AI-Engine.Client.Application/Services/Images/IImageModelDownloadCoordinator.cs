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

    /// <summary>
    ///     Requests cancellation of an in-flight download. Returns <see langword="true" /> when a running download was
    ///     signalled, <see langword="false" /> when nothing was in flight for that name (idempotent — cancelling an
    ///     already-finished download is not an error).
    /// </summary>
    /// <remarks>
    ///     Cancellation is cooperative: the transfer stops at its next checkpoint and the partial <c>.part</c> file is
    ///     deliberately left on disk so a later attempt resumes from it rather than restarting. An image file-set can be
    ///     tens of gigabytes, so a mis-started download that could not be stopped would occupy the node's bandwidth and
    ///     disk until it finished.
    /// </remarks>
    bool Cancel(string modelName);
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
    string? SanitizedError)
{
    /// <summary>
    ///     1-based index of the file currently transferring within the set, or <see langword="null" /> when the download
    ///     has not reported a part yet. Lets the UI say "part 2 of 3" instead of leaving the operator to guess why a
    ///     multi-gigabyte download is taking three passes.
    /// </summary>
    public int? PartIndex { get; init; }

    /// <summary>Number of files in the set, or <see langword="null" /> when not yet known.</summary>
    public int? PartCount { get; init; }
}
