namespace XE_Local_AI_Engine.Providers.Ollama;

using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions;

public sealed class OllamaLocalModelProvider : ILocalModelProvider, IDisposable
{
    public const string OllamaProviderName = "ollama";

    private readonly IOllamaApiClient _ollamaClient;
    private readonly SemaphoreSlim _pullSemaphore = new(1, 1);

    public OllamaLocalModelProvider(IOllamaApiClient ollamaClient)
    {
        _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
    }

    public void Dispose()
    {
        _pullSemaphore.Dispose();
    }

    public string ProviderName => OllamaProviderName;

    public async Task<ModelProviderHealth> CheckHealthAsync(CancellationToken ct)
    {
        try
        {
            var isRunning = await _ollamaClient.IsRunningAsync(ct).ConfigureAwait(false);
            return new ModelProviderHealth
            {
                ProviderName = ProviderName,
                IsHealthy = isRunning,
                ObservedAt = DateTimeOffset.UtcNow,
                Diagnostics = isRunning
                    ? ["Ollama endpoint is running."]
                    : ["Ollama endpoint did not report a running state."]
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ModelProviderHealth
            {
                ProviderName = ProviderName,
                IsHealthy = false,
                ObservedAt = DateTimeOffset.UtcNow,
                Diagnostics = [exception.Message]
            };
        }
    }

    public async Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken ct)
    {
        var models = await _ollamaClient.ListLocalModelsAsync(ct).ConfigureAwait(false);
        var descriptors = new List<LocalModelDescriptor>();

        foreach (var model in models)
        {
            var modelName = ReadModelName(model);
            if (string.IsNullOrWhiteSpace(modelName))
            {
                continue;
            }

            descriptors.Add(new LocalModelDescriptor
            {
                ModelName = modelName,
                ProviderName = ProviderName,
                IsAvailable = true,
                SizeBytes = model.Size,
                ModifiedAt = model.ModifiedAt,
                MaxContextTokens = await TryReadContextLengthAsync(modelName, ct).ConfigureAwait(false)
            });
        }

        return descriptors;
    }

    public async Task PullModelAsync(string modelName, IProgress<PullProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        await _pullSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await foreach (var response in _ollamaClient.PullModelAsync(modelName, ct).ConfigureAwait(false))
            {
                if (response is null)
                {
                    continue;
                }

                progress?.Report(new PullProgress
                {
                    ModelName = modelName,
                    Status = response.Status ?? string.Empty,
                    TotalBytes = response.Total,
                    CompletedBytes = response.Completed
                });
            }
        }
        finally
        {
            _pullSemaphore.Release();
        }
    }

    public Task DeleteModelAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return _ollamaClient.DeleteModelAsync(modelName, ct);
    }

    public Task WarmModelAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return _ollamaClient.ShowModelAsync(modelName, ct);
    }

    public Task UnloadModelAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return _ollamaClient.RequestModelUnloadAsync(modelName, ct);
    }

    public IChatClient CreateChatClient(LocalModelSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.ModelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.ProviderName);

        if (!string.Equals(selection.ProviderName, ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Provider selection '{selection.ProviderName}' does not match '{ProviderName}'.", nameof(selection));
        }

        return new OllamaApiClient(_ollamaClient.Uri, selection.ModelName);
    }

    private async Task<int?> TryReadContextLengthAsync(string modelName, CancellationToken ct)
    {
        try
        {
            var response = await _ollamaClient.ShowModelAsync(modelName, ct).ConfigureAwait(false);
            return OllamaModelInfoReader.TryGetContextLength(response.Info?.ExtraInfo, out var contextLength)
                ? contextLength
                : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadModelName(Model model)
    {
        return !string.IsNullOrWhiteSpace(model.ModelName) ? model.ModelName : model.Name;
    }
}
