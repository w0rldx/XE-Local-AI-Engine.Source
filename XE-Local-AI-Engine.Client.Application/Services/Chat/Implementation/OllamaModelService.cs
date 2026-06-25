namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Runtime.CompilerServices;
using OllamaSharp;
using OllamaSharp.Models;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;

public sealed class OllamaModelService : IOllamaModelService, IDisposable
{
    private readonly IOllamaApiClient _ollamaClient;
    private readonly SemaphoreSlim _pullSemaphore = new(initialCount: 1, maxCount: 1);

    public OllamaModelService(IOllamaApiClient ollamaClient)
    {
        _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
    }

    public void Dispose()
    {
        _pullSemaphore.Dispose();
    }

    public Task<IEnumerable<Model>> ListLocalModelsAsync(CancellationToken ct = default)
    {
        return _ollamaClient.ListLocalModelsAsync(ct);
    }

    public Task<ShowModelResponse> ShowModelAsync(string modelName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return _ollamaClient.ShowModelAsync(modelName, ct);
    }

    public async Task<OllamaModelDetails> ShowModelDetailsAsync(string modelName, CancellationToken ct = default)
    {
        var response = await ShowModelAsync(modelName, ct).ConfigureAwait(false);
        var maxContextTokens = OllamaModelInfoParser.TryGetContextLength(response.Info?.ExtraInfo, out var contextLength)
            ? contextLength
            : (int?)null;

        return new OllamaModelDetails(response, maxContextTokens, response.Capabilities ?? []);
    }

    public async IAsyncEnumerable<PullModelResponse> PullModelAsync(string modelName,
        [EnumeratorCancellation]
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        await _pullSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await foreach (var response in _ollamaClient.PullModelAsync(modelName, ct).ConfigureAwait(false))
            {
                if (response is not null)
                {
                    yield return response;
                }
            }
        }
        finally
        {
            _pullSemaphore.Release();
        }
    }

    public Task DeleteModelAsync(string modelName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return _ollamaClient.DeleteModelAsync(modelName, ct);
    }

    public async Task<IReadOnlyList<RunningModelSnapshot>> ListRunningModelsAsync(CancellationToken ct = default)
    {
        var runningModels = await _ollamaClient.ListRunningModelsAsync(ct).ConfigureAwait(false);
        return runningModels
               .Select(RunningModelSnapshotMapper.ToSnapshot)
               .ToArray();
    }

    public Task UnloadModelAsync(string modelName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        // keep_alive=0 sets the requested model's expiry timer to zero. Per Ollama's scheduler, an in-flight generation
        // completes before the model is evicted, so this is graceful. Unloading a model the runtime does not currently
        // hold is a harmless no-op, which keeps the eject action idempotent.
        return OllamaModelUnloader.UnloadAsync(_ollamaClient, modelName, ct);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            await _ollamaClient.ListLocalModelsAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
