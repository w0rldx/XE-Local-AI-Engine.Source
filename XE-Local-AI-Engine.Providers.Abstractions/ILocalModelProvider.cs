namespace XE_Local_AI_Engine.Providers.Abstractions;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

public interface ILocalModelProvider
{
    string ProviderName { get; }

    Task<ModelProviderHealth> CheckHealthAsync(CancellationToken ct);

    Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken ct);

    Task PullModelAsync(string modelName, IProgress<PullProgress>? progress, CancellationToken ct);

    Task DeleteModelAsync(string modelName, CancellationToken ct);

    Task WarmModelAsync(string modelName, CancellationToken ct);

    Task UnloadModelAsync(string modelName, CancellationToken ct);

    IChatClient CreateChatClient(LocalModelSelection selection);
}
