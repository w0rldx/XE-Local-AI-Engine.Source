namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public sealed class NullLlamaCppSourceBuildEventPublisher : ILlamaCppSourceBuildEventPublisher
{
    public Task PublishStatusAsync(LlamaCppSourceBuildStatusEvent statusEvent, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
