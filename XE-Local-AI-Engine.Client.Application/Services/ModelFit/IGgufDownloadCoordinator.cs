namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Coordinates operator-driven GGUF downloads for the advisor surface (Lane C3). It owns a per-model
///     <see cref="System.Threading.CancellationTokenSource" /> registry so a download started by one request can be
///     cancelled by a separate request, and it tracks the latest sanitized <see cref="GgufDownloadStatus" /> for each
///     in-flight or recently-finished download so the operator UI can poll progress.
///     <para>
///         <b>Honest cancellation:</b> cancel is cooperative — it signals the in-flight download's token, which the Lane B
///         store (<see cref="IGgufModelStore.EnsureModelAsync" />) honors at the next byte/await boundary. A download that
///         has already completed (or was never started) is a no-op cancel. No bytes are force-killed mid-write.
///     </para>
/// </summary>
public interface IGgufDownloadCoordinator
{
    /// <summary>
    ///     Begins (or rejoins) a background download for <paramref name="request" />. The download runs detached on the
    ///     application lifetime, reporting progress into the per-model status registry; the call returns immediately with
    ///     the canonical model-name identity to track/cancel it by. A download already in flight for the same model name
    ///     is rejoined (idempotent) rather than duplicated.
    /// </summary>
    GgufDownloadTicket Start(GgufModelRequest request);

    /// <summary>
    ///     Requests cancellation of the in-flight download for <paramref name="modelName" />. Returns <c>true</c> when a
    ///     cancellable download was found and signalled; <c>false</c> when no in-flight download exists for that model
    ///     name (already finished, never started). Idempotent.
    /// </summary>
    bool Cancel(string modelName);

    /// <summary>Returns the latest sanitized status for <paramref name="modelName" />, or <c>null</c> when unknown.</summary>
    GgufDownloadStatus? GetStatus(string modelName);
}

/// <summary>The accepted-download identity returned by <see cref="IGgufDownloadCoordinator.Start" />.</summary>
/// <param name="ModelName">Canonical <c>{repoId}[:{quant}]</c> model name the download is keyed by (track/cancel by this).</param>
/// <param name="AlreadyInFlight"><c>true</c> when an existing download for the same model name was rejoined instead of started.</param>
public sealed record GgufDownloadTicket(string ModelName, bool AlreadyInFlight);

/// <summary>Phase of a coordinated GGUF download.</summary>
public enum GgufDownloadPhase
{
    /// <summary>The download is running (bytes flowing or verifying).</summary>
    Running = 0,

    /// <summary>The download finished and the file verified present.</summary>
    Completed = 1,

    /// <summary>The download was cancelled cooperatively before completing.</summary>
    Cancelled = 2,

    /// <summary>The download failed; <see cref="GgufDownloadStatus.SanitizedError" /> carries an operator-safe reason.</summary>
    Failed = 3
}

/// <summary>
///     A sanitized snapshot of a coordinated download's progress. Carries only the model name, phase, byte counts and a
///     sanitized error reason — never an absolute path, URL, token, or raw store payload (plan §10).
/// </summary>
public sealed record GgufDownloadStatus(
    string ModelName,
    GgufDownloadPhase Phase,
    long? CompletedBytes,
    long? TotalBytes,
    string? SanitizedError);
