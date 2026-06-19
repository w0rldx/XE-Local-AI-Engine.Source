namespace XE_Local_AI_Engine.Providers.Ollama;

using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Ollama implementation of the provider-neutral local-model management and chat-client boundary.
/// </summary>
/// <remarks>
///     This adapter keeps OllamaSharp types inside the provider project. It normalizes health, model inventory, pull
///     progress, context-length probing, and chat-client creation into DTOs consumed by HostAgent, React, and the
///     application-layer agent runtime.
/// </remarks>
public sealed class OllamaLocalModelProvider : ILocalModelProvider, IDisposable
{
    /// <summary>Provider key used across persisted selections and capability payloads.</summary>
    public const string OllamaProviderName = "ollama";

    private readonly IOllamaApiClient _ollamaClient;
    private readonly SemaphoreSlim _pullSemaphore = new(1, 1);

    /// <summary>Creates a provider wrapper around the configured Ollama API client.</summary>
    public OllamaLocalModelProvider(IOllamaApiClient ollamaClient)
    {
        _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
    }

    /// <summary>Releases the pull gate semaphore held by this provider instance.</summary>
    public void Dispose()
    {
        _pullSemaphore.Dispose();
    }

    /// <inheritdoc />
    public string ProviderName => OllamaProviderName;

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task DeleteModelAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return _ollamaClient.DeleteModelAsync(modelName, ct);
    }

    /// <inheritdoc />
    public Task WarmModelAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return _ollamaClient.ShowModelAsync(modelName, ct);
    }

    /// <inheritdoc />
    public Task UnloadModelAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return OllamaModelUnloader.UnloadAsync(_ollamaClient, modelName, ct);
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LocalModelSelection selection)
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
