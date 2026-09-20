namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

/// <summary>
///     No-op <see cref="IGgufDownloadEventPublisher" />, registered as the default in <c>AddNodeModelFit</c> so the
///     download coordinator resolves a publisher even with no SignalR hub wired (Application-only and test hosts).
/// </summary>
/// <remarks>The Client host registers a hub-backed publisher that supersedes this one.</remarks>
internal sealed class NullGgufDownloadEventPublisher : IGgufDownloadEventPublisher
{
    public Task PublishStatusAsync(GgufDownloadStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
