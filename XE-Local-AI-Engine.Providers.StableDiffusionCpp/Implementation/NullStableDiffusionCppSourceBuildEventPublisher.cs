namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

internal sealed class NullStableDiffusionCppSourceBuildEventPublisher : IStableDiffusionCppSourceBuildEventPublisher
{
    public Task PublishStatusAsync(StableDiffusionCppSourceBuildStatusEvent statusEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(statusEvent);
        return Task.CompletedTask;
    }
}
