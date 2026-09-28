namespace XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     Publishes sanitized GGUF download status changes to connected operator clients, pushing the FULL sanitized
///     status payload so the React client updates its download view without a follow-up REST poll.
/// </summary>
/// <remarks>
///     Unlike the scheduler publisher, which is notification-only and triggers a refetch, this channel replaces the
///     per-second <c>GET model-fit/gguf/downloads</c> poll; the list endpoint remains for the one-shot hydrate on
///     mount. The default implementation is a no-op (<see cref="Implementation.NullGgufDownloadEventPublisher" />);
///     the Client host swaps in a hub-backed publisher (<c>GgufDownloadEventPublisher</c>) that owns the method name
///     and wire shape. The status is already sanitized: never a path, URL or token.
/// </remarks>
public interface IGgufDownloadEventPublisher
{
    /// <summary>Pushes the latest sanitized status for one tracked acquisition to all connected operator clients.</summary>
    Task PublishStatusAsync(GgufAcquisitionStatus status, CancellationToken cancellationToken = default);
}
