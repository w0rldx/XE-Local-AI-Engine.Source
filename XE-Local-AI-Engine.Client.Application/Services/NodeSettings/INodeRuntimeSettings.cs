namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     The single read surface migrated consumers use for user-editable runtime knobs (the
///     appsettings-to-node-settings migration). Each getter resolves the effective value with the precedence
///     <c>stored value &gt; appsettings seed &gt; hardcoded default</c>: it reads the cached
///     <see cref="INodeSettingsStore" /> (a sub-millisecond hit after the first load) and falls back to the appsettings
///     seed captured from the bound <c>IOptions&lt;T&gt;</c>/<c>IConfiguration</c> at construction, then to a hardcoded
///     default. Consumers must read migrated values through this surface, never via <c>IOptions&lt;T&gt;</c> of a
///     migrated field — the appsettings binding of a migrated section is the seed only.
/// </summary>
public interface INodeRuntimeSettings
{
    /// <summary>The effective local-chat default model id (stored &gt; <c>Agent:LocalChat:DefaultModel</c>).</summary>
    Task<string> GetDefaultModelNameAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the local-chat offer list includes tools (stored &gt; <c>Agent:LocalChat:EnableTools</c> &gt; true).</summary>
    Task<bool> GetEnableToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>The AgentHome tool-capable model allowlist (stored &gt; <c>AgentHome:ToolCapableModels</c>).</summary>
    Task<IReadOnlyList<string>> GetToolCapableModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>The Ollama runtime endpoint (stored &gt; <c>Ollama:Endpoint</c> &gt; loopback default).</summary>
    Task<string> GetOllamaEndpointAsync(CancellationToken cancellationToken = default);

    /// <summary>The Hugging Face default quant (stored &gt; <c>HuggingFace:DefaultQuant</c> &gt; "Q4_K_M").</summary>
    Task<string> GetHuggingFaceDefaultQuantAsync(CancellationToken cancellationToken = default);

    /// <summary>The Hugging Face disk-guard margin in bytes (stored &gt; <c>HuggingFace:DiskMarginBytes</c> &gt; 1 GiB).</summary>
    Task<long> GetHuggingFaceDiskMarginBytesAsync(CancellationToken cancellationToken = default);

    /// <summary>The max concurrently-loaded llama.cpp processes (stored &gt; seed 3).</summary>
    Task<int> GetLlamaMaxLoadedProcessesAsync(CancellationToken cancellationToken = default);

    /// <summary>The llama.cpp idle eviction TTL (stored &gt; seed 15 minutes).</summary>
    Task<TimeSpan> GetLlamaIdleTimeToLiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether periodic keep-warm is enabled (stored &gt; off).</summary>
    Task<bool> GetKeepModelWarmEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>The selected local chat model to keep resident, or <see langword="null" /> when unset.</summary>
    Task<string?> GetKeepModelWarmModelNameAsync(CancellationToken cancellationToken = default);

    /// <summary>The keep-warm touch cadence (stored &gt; 5 minutes).</summary>
    Task<TimeSpan> GetKeepModelWarmIntervalAsync(CancellationToken cancellationToken = default);

    /// <summary>The worker response-size cap in MiB (stored &gt; <c>WorkerNode:MaxResponseSizeMb</c> &gt; 10).</summary>
    Task<int> GetMaxResponseSizeMbAsync(CancellationToken cancellationToken = default);

    /// <summary>The recommended llama.cpp release tag (stored &gt; <c>LlamaCppReleasePins.PinnedTag</c> "b10201").</summary>
    Task<string> GetRecommendedLlamaCppTagAsync(CancellationToken cancellationToken = default);

    /// <summary>The orchestration idle-timeout in seconds (stored &gt; <c>Agent:Orchestration:IdleTimeoutSeconds</c> &gt; 120).</summary>
    Task<int> GetOrchestrationIdleTimeoutSecondsAsync(CancellationToken cancellationToken = default);

    /// <summary>The AgentHome prepare-phase timeout in seconds (stored &gt; <c>AgentHome:PrepareTimeoutSeconds</c> &gt; 900).</summary>
    Task<int> GetAgentHomePrepareTimeoutSecondsAsync(CancellationToken cancellationToken = default);

    /// <summary>The AgentHome per-command timeout in seconds (stored &gt; <c>AgentHome:CommandTimeoutSeconds</c> &gt; 300).</summary>
    Task<int> GetAgentHomeCommandTimeoutSecondsAsync(CancellationToken cancellationToken = default);

    /// <summary>The AgentHome per-folder byte budget (stored &gt; <c>AgentHome:MaxSelectedFolderBytes</c> &gt; 512 MiB).</summary>
    Task<long> GetAgentHomeMaxSelectedFolderBytesAsync(CancellationToken cancellationToken = default);

    /// <summary>The AgentHome exported-patch byte budget (stored &gt; <c>AgentHome:MaxPatchBytes</c> &gt; 50 MiB).</summary>
    Task<long> GetAgentHomeMaxPatchBytesAsync(CancellationToken cancellationToken = default);

    /// <summary>The pending tool-call max age in minutes (stored &gt; <c>WorkerNode:MaxPendingToolCallAgeMinutes</c> &gt; 10).</summary>
    Task<int> GetMaxPendingToolCallAgeMinutesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The disconnect grace in seconds before a run with no attached client is cancelled
    ///     (stored &gt; <c>WorkerNode:DetachedGraceSeconds</c> &gt; 300); <c>0</c> never cancels.
    /// </summary>
    Task<int> GetDetachedGraceSecondsAsync(CancellationToken cancellationToken = default);

    /// <summary>The chat-role prompt-cache prefix-reuse window in tokens (stored &gt; seed 256; <c>0</c> disables).</summary>
    Task<int> GetChatCacheReuseAsync(CancellationToken cancellationToken = default);

    /// <summary>The chat-role speculative-decoding <c>--spec-type</c> (stored &gt; seed <c>none</c>).</summary>
    Task<string> GetSpeculativeModeAsync(CancellationToken cancellationToken = default);

    /// <summary>The GPU chat-spawn KV-cache type <c>-ctk</c>/<c>-ctv</c> (stored &gt; seed <c>q8_0</c>).</summary>
    Task<string> GetKvCacheTypeAsync(CancellationToken cancellationToken = default);

    /// <summary>The installed draft-model name for <c>draft-*</c> modes, or <see langword="null" /> when unset.</summary>
    Task<string?> GetSpeculativeDraftModelNameAsync(CancellationToken cancellationToken = default);

    /// <summary>The draft tokens per step <c>--spec-draft-n-max</c> (stored &gt; seed 3; <c>0</c> omits the flag).</summary>
    Task<int> GetSpeculativeDraftMaxTokensAsync(CancellationToken cancellationToken = default);

    /// <summary>The draft-model GPU layers <c>--spec-draft-ngl</c>, or <see langword="null" /> when unset (flag omitted).</summary>
    Task<int?> GetSpeculativeDraftGpuLayersAsync(CancellationToken cancellationToken = default);

    /// <summary>The knowledge-base reranker model name (stored &gt; off), or <see langword="null" /> when reranking is disabled.</summary>
    Task<string?> GetRerankerModelNameAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The node-local chat model a FAST <c>auto</c> turn may be moved onto (stored &gt; off), or
    ///     <see langword="null" /> when this node names none. Read per send rather than at host build, so a save
    ///     applies to the next turn without a restart.
    /// </summary>
    Task<string?> GetAutoEffortFastModelNameAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Whether the user-defined custom tools feature is enabled at the node level (stored &gt; off). Default is
    ///     <see langword="false" /> — a host-execution feature is opt-in. When off, custom tools are neither offered nor
    ///     resolvable.
    /// </summary>
    Task<bool> GetCustomToolsEnabledAsync(CancellationToken cancellationToken = default);

    // Synchronous twins for the composition/startup path (DI factory seeds + singleton constructors) and for
    // request-time call sites that are structurally synchronous. These read the stored settings synchronously to avoid
    // blocking on async file I/O during host startup, which starves the thread pool.
    //
    // Prefer the async getters. Use a sync twin at request time ONLY when the call site cannot be made async without
    // rippling through an interface — the live example is LocalToolOfferProvider.IsToolCapable, whose whole offer seam
    // is synchronous by design. That is safe because the read resolves through CachedNodeSettingsStore, where Load is an
    // IMemoryCache.TryGetValue hit and SaveAsync invalidates AND re-primes the entry; the file is touched only on a cold
    // first read. What is NOT acceptable is a sync twin on a per-TOKEN path, or capturing the result in a singleton
    // field to avoid the read — the latter is what silently required a node restart before an edit took effect.

    /// <inheritdoc cref="GetDefaultModelNameAsync" />
    string GetDefaultModelName();

    /// <inheritdoc cref="GetToolCapableModelsAsync" />
    IReadOnlyList<string> GetToolCapableModels();

    /// <inheritdoc cref="GetOllamaEndpointAsync" />
    string GetOllamaEndpoint();

    /// <inheritdoc cref="GetHuggingFaceDefaultQuantAsync" />
    string GetHuggingFaceDefaultQuant();

    /// <inheritdoc cref="GetHuggingFaceDiskMarginBytesAsync" />
    long GetHuggingFaceDiskMarginBytes();

    /// <inheritdoc cref="GetLlamaMaxLoadedProcessesAsync" />
    int GetLlamaMaxLoadedProcesses();

    /// <inheritdoc cref="GetLlamaIdleTimeToLiveAsync" />
    TimeSpan GetLlamaIdleTimeToLive();

    /// <inheritdoc cref="GetMaxResponseSizeMbAsync" />
    int GetMaxResponseSizeMb();

    /// <inheritdoc cref="GetOrchestrationIdleTimeoutSecondsAsync" />
    int GetOrchestrationIdleTimeoutSeconds();

    /// <inheritdoc cref="GetMaxPendingToolCallAgeMinutesAsync" />
    int GetMaxPendingToolCallAgeMinutes();

    /// <inheritdoc cref="GetDetachedGraceSecondsAsync" />
    int GetDetachedGraceSeconds();

    /// <inheritdoc cref="GetChatCacheReuseAsync" />
    int GetChatCacheReuse();

    /// <inheritdoc cref="GetSpeculativeModeAsync" />
    string GetSpeculativeMode();

    /// <inheritdoc cref="GetKvCacheTypeAsync" />
    string GetKvCacheType();

    /// <inheritdoc cref="GetSpeculativeDraftModelNameAsync" />
    string? GetSpeculativeDraftModelName();

    /// <inheritdoc cref="GetSpeculativeDraftMaxTokensAsync" />
    int GetSpeculativeDraftMaxTokens();

    /// <inheritdoc cref="GetSpeculativeDraftGpuLayersAsync" />
    int? GetSpeculativeDraftGpuLayers();

    /// <inheritdoc cref="GetRerankerModelNameAsync" />
    string? GetRerankerModelName();
}
