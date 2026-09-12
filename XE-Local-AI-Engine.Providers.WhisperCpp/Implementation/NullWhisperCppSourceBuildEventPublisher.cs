namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     The publisher floor. The provider owns no hub, so progress is observed by polling the status endpoint; this
///     keeps the build service's dependency satisfiable in a provider-only host.
/// </summary>
internal sealed class NullWhisperCppSourceBuildEventPublisher : IWhisperCppSourceBuildEventPublisher
{
    public Task PublishStatusAsync(WhisperCppSourceBuildStatusEvent statusEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(statusEvent);
        return Task.CompletedTask;
    }
}
