namespace XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     Publishes sanitized GGUF download status changes to connected operator clients. Unlike the scheduler publisher
///     (which is notification-only and triggers a refetch), this channel pushes the FULL sanitized status payload so the
///     React client updates its download view directly without a follow-up REST poll — it replaces the per-second
///     <c>GET model-fit/gguf/downloads</c> poll. The list endpoint remains for the one-shot hydrate on mount.
///     <para>
///         The default implementation is a no-op (<see cref="Implementation.NullGgufDownloadEventPublisher" />); the
///         Client host swaps in a hub-backed publisher (<c>GgufDownloadEventPublisher</c> over the
///         <c>GgufDownloadHub</c>). Payloads are sanitized at the broadcast boundary — never a path, URL, or token.
///     </para>
/// </summary>
public interface IGgufDownloadEventPublisher
{
    /// <summary>Pushes the latest sanitized status for one tracked download to all connected operator clients.</summary>
    Task PublishStatusAsync(GgufDownloadStatusHubEvent statusEvent, CancellationToken cancellationToken = default);
}

/// <summary>
///     Stable SignalR client-method name for download status pushes. The React client subscribes to this single method;
///     each push carries the full sanitized status, so the client reconciles by model name with no refetch.
/// </summary>
public static class GgufDownloadHubEvents
{
    public const string StatusChanged = "ggufDownload.statusChanged";
}

/// <summary>
///     Sanitized download-status push payload. Mirrors the safe fields of the REST <c>GgufDownloadStatusResponse</c>
///     (model name, phase string, byte counts, sanitized error) so the client reconciles a push exactly as it would a
///     list item — never an absolute path, URL, token, or raw store payload. <see cref="Phase" /> is the
///     <see cref="GgufDownloadPhase" /> name (<c>Running</c>/<c>Completed</c>/<c>Cancelled</c>/<c>Failed</c>).
/// </summary>
public sealed record GgufDownloadStatusHubEvent(
    string ModelName,
    string Phase,
    long? CompletedBytes,
    long? TotalBytes,
    string? SanitizedError,
    Guid OperationId = default,
    string OperationKind = "Download",
    string? ErrorCode = null,
    DateTimeOffset? UpdatedAtUtc = null);
