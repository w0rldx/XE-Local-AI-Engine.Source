namespace XE_Local_AI_Engine.Services.Chat;

public interface IOllamaModelService
{
    Task<IEnumerable<OllamaSharp.Models.Model>> ListLocalModelsAsync(CancellationToken ct = default);
    Task<OllamaSharp.Models.ShowModelResponse> ShowModelAsync(string modelName, CancellationToken ct = default);
    IAsyncEnumerable<OllamaSharp.Models.PullModelResponse> PullModelAsync(string modelName, CancellationToken ct = default);
    Task DeleteModelAsync(string modelName, CancellationToken ct = default);
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}
