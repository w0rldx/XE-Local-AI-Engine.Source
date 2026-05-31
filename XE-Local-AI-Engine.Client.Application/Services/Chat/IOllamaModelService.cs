namespace XE_Local_AI_Engine.Client.Services.Chat;

using OllamaSharp.Models;

/// <summary>
///     Application service for i ollama model behavior.
/// </summary>
public interface IOllamaModelService
{
    Task<IEnumerable<Model>> ListLocalModelsAsync(CancellationToken ct = default);
    Task<ShowModelResponse> ShowModelAsync(string modelName, CancellationToken ct = default);
    Task<OllamaModelDetails> ShowModelDetailsAsync(string modelName, CancellationToken ct = default);
    IAsyncEnumerable<PullModelResponse> PullModelAsync(string modelName, CancellationToken ct = default);
    Task DeleteModelAsync(string modelName, CancellationToken ct = default);
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}
