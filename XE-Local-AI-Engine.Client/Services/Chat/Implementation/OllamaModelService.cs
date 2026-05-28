namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Services.Chat;

using System.Runtime.CompilerServices;
using OllamaSharp;
using OllamaSharp.Models;

public sealed class OllamaModelService : IOllamaModelService, IDisposable
{
    private readonly IOllamaApiClient _ollamaClient;
    private readonly SemaphoreSlim _pullSemaphore = new(1, 1);

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

        return new OllamaModelDetails(response, maxContextTokens);
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
