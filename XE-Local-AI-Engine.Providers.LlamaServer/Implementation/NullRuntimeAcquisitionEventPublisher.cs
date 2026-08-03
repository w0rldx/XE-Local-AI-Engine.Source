namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     No-op <see cref="IRuntimeAcquisitionEventPublisher" /> registered by default in the provider stack. The Client
///     host replaces it with a hub-backed publisher; without a host (tests / headless / Aspire / CI) acquisition
///     progress is simply not broadcast, so those hosts stay byte-behavior-identical.
/// </summary>
public sealed class NullRuntimeAcquisitionEventPublisher : IRuntimeAcquisitionEventPublisher
{
    /// <inheritdoc />
    public Task PublishStatusAsync(RuntimeAcquisitionStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
