namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

/// <summary>
///     No-op <see cref="IGgufDownloadEventPublisher" />. Registered as the default in <c>AddNodeModelFit</c> so the
///     download coordinator resolves a publisher even when no SignalR hub is wired (Application-only and test hosts).
///     The Client host registers a hub-backed publisher that supersedes this one.
/// </summary>
internal sealed class NullGgufDownloadEventPublisher : IGgufDownloadEventPublisher
{
    public Task PublishStatusAsync(GgufDownloadStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
