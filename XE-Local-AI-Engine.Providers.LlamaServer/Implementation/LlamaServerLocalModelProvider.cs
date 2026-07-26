namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

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
    private readonly TimeSpan _networkTimeout;
    private readonly ILlamaServerProcessSupervisor _supervisor;

    /// <summary>
    ///     Creates the provider over the process supervisor and the GGUF model store. The supervisor options supply the
    ///     explicit per-call HTTP network timeout (AUD4-18) the deferred chat/embedding clients pin on the built OpenAI
    ///     client; a null options bag falls back to the default policy.
    /// </summary>
    public LlamaServerLocalModelProvider(ILlamaServerProcessSupervisor supervisor, IGgufModelStore modelStore, LlamaServerSupervisorOptions? options = null)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _networkTimeout = (options ?? new LlamaServerSupervisorOptions()).HttpNetworkTimeout;
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
    /// <remarks>
    ///     Warm-up is intentionally chat-only: it targets the interactive path. Pre-spawning embedding and reranker
    ///     processes would consume shared loaded-process slots for work that may never arrive; those roles remain
    ///     demand-started by their respective clients.
    /// </remarks>
    public async Task WarmModelAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        await _supervisor.EnsureRunningAsync(modelName, ModelRole.Chat, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Reports the running CHAT process's effective context window (the launched <c>-c</c> as clamped by the server,
    ///     read from <c>/props</c>). Returns <see langword="null" /> when no chat process is running for the model or its
    ///     effective context could not be read — the caller then falls back to the app-side default window. Synchronous
    ///     in-memory read on the supervisor; wrapped in a completed task to satisfy the async contract.
    /// </remarks>
    public Task<LocalModelRuntimeInfo?> GetRuntimeInfoAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        var info = _supervisor.GetRuntimeInfo(modelName, ModelRole.Chat);
        return Task.FromResult(info is null ? null : new LocalModelRuntimeInfo(info.EffectiveContextTokens));
    }

    /// <inheritdoc />
    public async Task UnloadModelAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        // A model may have chat, embedding, and reranker processes; evict every defined role. Eviction is idempotent.
        await _supervisor.EvictAllRolesAsync(modelName, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IChatClient CreateChatClient(LocalModelSelection selection)
    {
        ValidateSelection(selection);
        return new DeferredLlamaServerChatClient(_supervisor, selection.ModelName, _networkTimeout);
    }

    /// <inheritdoc />
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LocalModelSelection selection)
    {
        ValidateSelection(selection);
        return new DeferredLlamaServerEmbeddingGenerator(_supervisor, selection.ModelName, _networkTimeout);
    }

    private void ValidateSelection(LocalModelSelection selection)
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
    }
}
