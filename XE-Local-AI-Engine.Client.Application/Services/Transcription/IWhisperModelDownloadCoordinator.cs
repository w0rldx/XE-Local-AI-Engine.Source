namespace XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>The accepted-download identity returned by <see cref="IWhisperModelDownloadCoordinator.Start" />.</summary>
/// <param name="ModelId">The catalogue id the download is keyed by; poll its status by this.</param>
/// <param name="AlreadyInFlight"><c>true</c> when an existing download was rejoined instead of started.</param>
public sealed record WhisperModelDownloadTicket(string ModelId, bool AlreadyInFlight);

/// <summary>Phase of a coordinated Whisper weight download.</summary>
public enum WhisperModelDownloadPhase
{
    /// <summary>Bytes are flowing, or a file is being verified.</summary>
    Running = 0,

    /// <summary>Every file downloaded and verified.</summary>
    Completed = 1,

    /// <summary>Cancelled cooperatively before completing.</summary>
    Cancelled = 2,

    /// <summary>Failed; <see cref="WhisperModelDownloadStatus.SanitizedError" /> carries an operator-safe reason.</summary>
    Failed = 3
}

/// <summary>
///     A sanitized snapshot of one coordinated download. Carries the catalogue id, the phase, byte counts and a
///     sanitized reason — never an absolute path, a URL, or a token.
/// </summary>
public sealed record WhisperModelDownloadStatus(
    string ModelId,
    WhisperModelDownloadPhase Phase,
    long? CompletedBytes,
    long? TotalBytes,
    string? SanitizedError)
{
    /// <summary>1-based index of the file currently transferring, or <see langword="null" /> before the first report.</summary>
    public int? PartIndex { get; init; }

    /// <summary>Number of files in this download.</summary>
    public int? PartCount { get; init; }
}

/// <summary>
///     Coordinates operator-driven Whisper weight downloads. The transfer runs detached and outlives the request that
///     started it, and the coordinator keeps the latest sanitized status per catalogue id for the UI to poll.
/// </summary>
/// <remarks>
///     The point of the type is that every download ends in an OBSERVABLE terminal phase. A detached transfer that
///     swallowed its failure into a log line would leave the operator unable to tell "still fetching 1.6 GB" from
///     "failed ten minutes ago".
/// </remarks>
public interface IWhisperModelDownloadCoordinator
{
    /// <summary>
    ///     Begins, or rejoins, a background download of <paramref name="modelId" />'s weights. Returns once the
    ///     download is registered. A download already in flight for the same id is rejoined rather than duplicated, so
    ///     a double submit cannot start two transfers.
    /// </summary>
    /// <exception cref="ArgumentException">The id is not in the catalogue.</exception>
    WhisperModelDownloadTicket Start(string modelId);

    /// <summary>The latest sanitized status for <paramref name="modelId" />, or <see langword="null" /> when unknown.</summary>
    WhisperModelDownloadStatus? GetStatus(string modelId);

    /// <summary>A snapshot of every tracked download status, in flight and recently finished.</summary>
    IReadOnlyList<WhisperModelDownloadStatus> ListStatuses();

    /// <summary>
    ///     Requests cancellation of an in-flight download. Returns <see langword="true" /> when one was signalled and
    ///     <see langword="false" /> when nothing was in flight — cancelling a finished download is not an error.
    /// </summary>
    bool Cancel(string modelId);
}
