namespace XE_Local_AI_Engine.Providers.Abstractions;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

/// <summary>
///     Provider-neutral contract for local model runtimes exposed to the node host.
/// </summary>
/// <remarks>
///     Implementations translate runtime-specific APIs (for example Ollama's model-management and chat endpoints)
///     into the stable host-agent DTOs plus a <see cref="IChatClient" />. Keep this boundary free of provider
///     transport types so the React UI and platform capability payloads can remain provider-agnostic.
/// </remarks>
public interface ILocalModelProvider
{
    /// <summary>Stable provider key used in capability payloads and model selections.</summary>
    string ProviderName { get; }

    /// <summary>Checks whether the provider endpoint is reachable and ready to serve model requests.</summary>
    Task<ModelProviderHealth> CheckHealthAsync(CancellationToken ct);

    /// <summary>Lists locally available models in the normalized host-agent descriptor shape.</summary>
    Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken ct);

    /// <summary>Downloads or updates a model and reports provider-specific byte/status progress when available.</summary>
    Task PullModelAsync(string modelName, IProgress<PullProgress>? progress, CancellationToken ct);

    /// <summary>Deletes a locally installed model.</summary>
    Task DeleteModelAsync(string modelName, CancellationToken ct);

    /// <summary>Loads or probes a model so first-token latency is paid before an interactive turn.</summary>
    Task WarmModelAsync(string modelName, CancellationToken ct);

    /// <summary>Requests provider-side model unload when the runtime supports releasing loaded weights.</summary>
    Task UnloadModelAsync(string modelName, CancellationToken ct);

    /// <summary>
    ///     Creates a chat client for the selected provider/model pair. Callers must pass a selection whose
    ///     <see cref="LocalModelSelection.ProviderName" /> matches <see cref="ProviderName" />.
    /// </summary>
    IChatClient CreateChatClient(LocalModelSelection selection);
}
