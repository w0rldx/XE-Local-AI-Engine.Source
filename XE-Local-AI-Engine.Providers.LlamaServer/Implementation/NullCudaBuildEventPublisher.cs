namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     No-op <see cref="ICudaBuildEventPublisher" /> registered by default in the provider stack. The Client host replaces
///     it with a hub-backed publisher; without a host (tests / headless) build progress is simply not broadcast.
/// </summary>
public sealed class NullCudaBuildEventPublisher : ICudaBuildEventPublisher
{
    /// <inheritdoc />
    public Task PublishStatusAsync(CudaBuildStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
