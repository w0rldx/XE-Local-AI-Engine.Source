namespace XE_Local_AI_Engine.Client.Services.Images.Implementation;

/// <summary>
///     No-op default <see cref="IImageJobEventPublisher" />: image-job status changes are still persisted and served by
///     the status endpoints, but nothing is pushed. The Client host replaces this with the hub-backed publisher.
/// </summary>
public sealed class NullImageJobEventPublisher : IImageJobEventPublisher
{
    public Task PublishStatusAsync(ImageJobStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
