namespace XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     Publishes sanitized GGUF download status changes to connected operator clients, pushing the FULL sanitized
///     status payload so the React client updates its download view without a follow-up REST poll.
/// </summary>
/// <remarks>
///     Unlike the scheduler publisher, which is notification-only and triggers a refetch, this channel replaces the
///     per-second <c>GET model-fit/gguf/downloads</c> poll; the list endpoint remains for the one-shot hydrate on
///     mount. The default implementation is a no-op (<see cref="Implementation.NullGgufDownloadEventPublisher" />);
///     the Client host swaps in a hub-backed publisher (<c>GgufDownloadEventPublisher</c> over
///     <c>GgufDownloadHub</c>). Payloads are sanitized at the broadcast boundary — never a path, URL or token.
/// </remarks>
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
///     Sanitized download-status push payload: the safe fields of the REST <c>GgufDownloadStatusResponse</c> (model
///     name, phase string, byte counts, sanitized error), so the client reconciles a push exactly as it would a list
///     item.
/// </summary>
/// <remarks>
///     Never an absolute path, URL, token or raw store payload. <see cref="Phase" /> is the
///     <see cref="GgufDownloadPhase" /> name (<c>Running</c>/<c>Completed</c>/<c>Cancelled</c>/<c>Failed</c>).
/// </remarks>
public sealed class GgufDownloadStatusHubEvent
{
    public required string ModelName { get; init; }

    public required string Phase { get; init; }

    public required long? CompletedBytes { get; init; }

    public required long? TotalBytes { get; init; }

    public required string? SanitizedError { get; init; }

    public Guid OperationId { get; init; }

    public string OperationKind { get; init; } = "Download";

    public string? ErrorCode { get; init; }

    public DateTimeOffset? UpdatedAtUtc { get; init; }
}
