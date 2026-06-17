namespace XE_Local_AI_Engine.Providers.LlamaServer;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     llama-server implementation of the provider-neutral <see cref="ILocalModelProvider" /> boundary (plan §7.3).
///     Maps the 8-member contract onto the process supervisor (chat/embedding runtime, warm/unload, health) and the
///     GGUF model store (installed-model inventory + file resolution). <see cref="ProviderName" /> is
///     <c>"llamacpp"</c>.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="CreateChatClient" /> / <see cref="CreateEmbeddingGenerator" /> return <em>deferred</em> clients
///         (<see cref="DeferredLlamaServerChatClient" /> / <see cref="DeferredLlamaServerEmbeddingGenerator" />) that
///         ensure-run the right <c>(model, role)</c> process on first use, so cold-start is a normal first-token delay
///         rather than a blocking sync factory call (plan §7.4). The selection's <see cref="LocalModelSelection.ProviderName" />
///         must equal <see cref="ProviderName" /> (mirror of <c>OllamaLocalModelProvider</c>).
///     </para>
///     <para>
///         <strong>GGUF acquisition (pull/delete) is Lane B's <see cref="IGgufModelStore" />.</strong> The store
///         contract Lane A consumes exposes only file resolution + installed-model enumeration, so
///         <see cref="PullModelAsync" /> / <see cref="DeleteModelAsync" /> surface a clear, sanitized
///         <see cref="LlamaRuntimeException" /> until Lane B lands the download/delete surface.
///     </para>
/// </remarks>
public sealed class LlamaServerLocalModelProvider : ILocalModelProvider
{
    private readonly ILlamaServerProcessSupervisor _supervisor;
    private readonly IGgufModelStore _modelStore;

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
                ? new[] { "llama-server supervisor is operational with no loaded models." }
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
    public Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken ct) =>
        _modelStore.ListInstalledModelsAsync(ct);

    /// <inheritdoc />
    /// <remarks>
    ///     GGUF download is Lane B's responsibility (<see cref="IGgufModelStore" /> exposes no pull surface to Lane A).
    ///     Surfaces a sanitized not-yet-available error rather than silently no-op'ing.
    /// </remarks>
    public Task PullModelAsync(string modelName, IProgress<PullProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        throw new LlamaRuntimeException(
            "Downloading GGUF models is handled by the model store and is not yet available in this build.");
    }

    /// <inheritdoc />
    /// <remarks>GGUF deletion is Lane B's responsibility; surfaces a sanitized not-yet-available error.</remarks>
    public Task DeleteModelAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        throw new LlamaRuntimeException(
            "Deleting GGUF models is handled by the model store and is not yet available in this build.");
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
            throw new ArgumentException(
                $"Provider selection '{selection.ProviderName}' does not match '{ProviderName}'.", nameof(selection));
        }
    }
}
