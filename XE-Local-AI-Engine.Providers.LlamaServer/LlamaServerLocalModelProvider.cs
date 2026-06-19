namespace XE_Local_AI_Engine.Providers.LlamaServer;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     llama-server implementation of the provider-neutral <see cref="ILocalModelProvider" /> boundary.
///     Maps the 8-member contract onto the process supervisor (chat/embedding runtime, warm/unload, health) and the
///     GGUF model store (installed-model inventory + file resolution). <see cref="ProviderName" /> is
///     <c>"llamacpp"</c>.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="CreateChatClient" /> / <see cref="CreateEmbeddingGenerator" /> return <em>deferred</em> clients
///         (<see cref="DeferredLlamaServerChatClient" /> / <see cref="DeferredLlamaServerEmbeddingGenerator" />) that
///         ensure-run the right <c>(model, role)</c> process on first use, so cold-start is a normal first-token delay
///         rather than a blocking sync factory call. The selection's <see cref="LocalModelSelection.ProviderName" />
///         must equal <see cref="ProviderName" /> (mirror of <c>OllamaLocalModelProvider</c>).
///     </para>
///     <para>
///         <strong>GGUF acquisition (pull/delete) is owned by the GGUF model store (<see cref="IGgufModelStore" />).</strong> The store
///         contract this provider consumes exposes file resolution + installed-model enumeration (to launch and list), and
///         <see cref="PullModelAsync" /> / <see cref="DeleteModelAsync" /> route into the store's download/delete surface.
///     </para>
/// </remarks>
public sealed class LlamaServerLocalModelProvider : ILocalModelProvider
{
    private readonly IGgufModelStore _modelStore;
    private readonly ILlamaServerProcessSupervisor _supervisor;

    /// <summary>Creates the provider over the process supervisor and the GGUF model store.</summary>
    public LlamaServerLocalModelProvider(ILlamaServerProcessSupervisor supervisor, IGgufModelStore modelStore)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
    }

    /// <inheritdoc />
    public string ProviderName => LlamaServerProviderConstants.ProviderName;

    /// <inheritdoc />
    public async Task<ModelProviderHealth> CheckHealthAsync(CancellationToken ct)
    {
        try
        {
            var processHealths = await _supervisor.CheckHealthAsync(ct).ConfigureAwait(false);

            // The provider is healthy iff the supervisor is operational (it answered the aggregation). Per-process
            // detail is surfaced for diagnostics; an empty list means "operational, no processes loaded yet".
            var diagnostics = processHealths.Count == 0
                ? new[]
                {
                    "llama-server supervisor is operational with no loaded models."
                }
                : processHealths
                  .Select(static health =>
                      $"{health.ModelName} ({health.Role}): {(health.IsResponsive ? "responsive" : "unresponsive")} — {health.Detail}")
                  .ToArray();

            return new ModelProviderHealth
            {
                ProviderName = ProviderName,
                IsHealthy = true,
                ObservedAt = DateTimeOffset.UtcNow,
                Diagnostics = diagnostics
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
    public Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken ct)
    {
        return _modelStore.ListInstalledModelsAsync(ct);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Delegates to the GGUF model store's <see cref="IGgufModelStore.EnsureModelAsync" />. The bare model name is parsed into a
    ///     <see cref="GgufModelRequest" /> via the shared <see cref="GgufModelName" /> convention (<c>{repo}[:{quant}]</c>);
    ///     the store reports byte/status <see cref="PullProgress" /> 1:1.
    /// </remarks>
    public Task PullModelAsync(string modelName, IProgress<PullProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        var request = GgufModelName.Parse(modelName);
        return _modelStore.EnsureModelAsync(request, progress, ct);
    }

    /// <inheritdoc />
    /// <remarks>Delegates to the GGUF model store's <see cref="IGgufModelStore.DeleteModelAsync" /> (file + registry entry).</remarks>
    public Task DeleteModelAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return _modelStore.DeleteModelAsync(modelName, ct);
    }

    /// <inheritdoc />
    public async Task WarmModelAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        await _supervisor.EnsureRunningAsync(modelName, ModelRole.Chat, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UnloadModelAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        // A model may have both a chat and an embedding process; evict both roles. Eviction is idempotent.
        await _supervisor.EvictAsync(modelName, ModelRole.Chat, ct).ConfigureAwait(false);
        await _supervisor.EvictAsync(modelName, ModelRole.Embedding, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IChatClient CreateChatClient(LocalModelSelection selection)
    {
        ValidateSelection(selection);
        return new DeferredLlamaServerChatClient(_supervisor, selection.ModelName);
    }

    /// <inheritdoc />
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LocalModelSelection selection)
    {
        ValidateSelection(selection);
        return new DeferredLlamaServerEmbeddingGenerator(_supervisor, selection.ModelName);
    }

    private void ValidateSelection(LocalModelSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.ModelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.ProviderName);

        if (!string.Equals(selection.ProviderName, ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Provider selection '{selection.ProviderName}' does not match '{ProviderName}'.", nameof(selection));
        }
    }
}
