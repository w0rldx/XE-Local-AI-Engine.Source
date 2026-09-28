namespace XE_Local_AI_Engine.Client.Hubs;

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
///     <c>GgufAcquisitionPhase</c> name and <see cref="OperationKind" /> the <c>GgufAcquisitionOperationKind</c> name.
/// </remarks>
internal sealed class GgufDownloadStatusHubMessage
{
    public required string ModelName { get; init; }

    public required string Phase { get; init; }

    public required long? CompletedBytes { get; init; }

    public required long? TotalBytes { get; init; }

    public required string? SanitizedError { get; init; }

    public required Guid OperationId { get; init; }

    public required string OperationKind { get; init; }

    public required string? ErrorCode { get; init; }

    public required DateTimeOffset? UpdatedAtUtc { get; init; }
}
