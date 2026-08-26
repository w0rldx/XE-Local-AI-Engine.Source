namespace XE_Local_AI_Engine.Providers.Ollama.Implementation;

using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Ollama implementation of the provider-neutral local-model management and chat-client boundary.
/// </summary>
/// <remarks>
///     This adapter keeps OllamaSharp types inside the provider project. It normalizes health, model inventory, pull
///     progress, context-length probing, and chat-client creation into DTOs consumed by React and the application
///     layer.
/// </remarks>
public sealed class OllamaLocalModelProvider : ILocalModelProvider, IDisposable
{
    /// <summary>Provider key used across persisted selections and capability payloads.</summary>
    public const string OllamaProviderName = "ollama";

    private readonly IOllamaApiClient _ollamaClient;
    private readonly OllamaApiClientFactory _clientFactory;
    private readonly SemaphoreSlim _pullSemaphore = new(initialCount: 1, maxCount: 1);

    /// <summary>
    ///     Creates a provider wrapper around the configured Ollama management client and the client factory that mints
    ///     per-model chat/embedding clients over the same hardened transport.
    /// </summary>
    public OllamaLocalModelProvider(IOllamaApiClient ollamaClient, OllamaApiClientFactory clientFactory)
    {
        _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
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
            var modelName = model.ReadModelName();
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
                // Explicit ctor (MA0132) with the SAME semantics as the implicit DateTime->DateTimeOffset
                // conversion, so the value is byte-identical regardless of model.ModifiedAt.Kind.
                ModifiedAt = new DateTimeOffset(model.ModifiedAt),
                RevisionFingerprint = model.Digest,
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
    /// <remarks>
    ///     Preloads the model via Ollama's documented warm-up mechanism: an empty <c>/api/generate</c> request, which
    ///     loads the weights and returns immediately without generating tokens (there is no prompt to complete).
    ///     <c>keep_alive</c> is deliberately omitted, so residency follows Ollama's keep_alive default instead of
    ///     pinning the model. Note <c>/api/show</c> is NOT a warm-up — it only reads metadata and never loads weights.
    /// </remarks>
    public async Task WarmModelAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var request = new GenerateRequest
        {
            Model = modelName,
            Prompt = string.Empty,
            Stream = false
        };

        // Fully enumerate the (single, non-stream) response so the request is actually dispatched — GenerateAsync is a
        // streaming method and sends no HTTP call until the enumerator is drained. Mirrors OllamaModelUnloader, which
        // uses the same request shape with keep_alive=0 for the inverse (eviction) side effect.
        await foreach (var chunk in _ollamaClient.GenerateAsync(request, ct).ConfigureAwait(false))
        {
            _ = chunk;
        }
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
        if (string.IsNullOrWhiteSpace(selection.ModelName))
        {
            throw new ArgumentException("Model selection must include a model name.", nameof(selection));
        }

        if (string.IsNullOrWhiteSpace(selection.ProviderName))
        {
            throw new ArgumentException("Model selection must include a provider name.", nameof(selection));
        }

        if (!string.Equals(selection.ProviderName, ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Provider selection '{selection.ProviderName}' does not match '{ProviderName}'.", nameof(selection));
        }

        // Mint over the shared hardened transport so routed sends inherit the fail-fast connect bound and
        // unreachable-daemon normalization, rather than a raw default OllamaApiClient(uri, model) transport.
        return _clientFactory.CreateClient(selection.ModelName);
    }

    /// <inheritdoc />
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LocalModelSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (string.IsNullOrWhiteSpace(selection.ModelName))
        {
            throw new ArgumentException("Model selection must include a model name.", nameof(selection));
        }

        if (string.IsNullOrWhiteSpace(selection.ProviderName))
        {
            throw new ArgumentException("Model selection must include a provider name.", nameof(selection));
        }

        if (!string.Equals(selection.ProviderName, ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Provider selection '{selection.ProviderName}' does not match '{ProviderName}'.", nameof(selection));
        }

        // Mint over the shared hardened transport (see CreateChatClient) so embedding calls fail fast against an absent
        // daemon instead of hanging on the default connect timeout.
        return _clientFactory.CreateClient(selection.ModelName);
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
}
