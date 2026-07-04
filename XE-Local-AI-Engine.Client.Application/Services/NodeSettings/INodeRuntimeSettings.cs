namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

using XE_Local_AI_Engine.Client.Models;

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

    /// <summary>The worker response-size cap in MiB (stored &gt; <c>WorkerNode:MaxResponseSizeMb</c> &gt; 10).</summary>
    Task<int> GetMaxResponseSizeMbAsync(CancellationToken cancellationToken = default);

    /// <summary>The recommended llama.cpp release tag (stored &gt; <c>LlamaCppReleasePins.PinnedTag</c> "b9692").</summary>
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
    ///     The node-level sampling defaults, or <see langword="null" /> when none are configured (today's behavior). There
    ///     is no appsettings seed: this is a developer-only stored-or-nothing knob.
    /// </summary>
    Task<SamplingOptions?> GetSamplingDefaultsAsync(CancellationToken cancellationToken = default);

    /// <summary>The chat-role prompt-cache prefix-reuse window in tokens (stored &gt; seed 256; <c>0</c> disables).</summary>
    Task<int> GetChatCacheReuseAsync(CancellationToken cancellationToken = default);

    /// <summary>The chat-role speculative-decoding <c>--spec-type</c> (stored &gt; seed <c>none</c>).</summary>
    Task<string> GetSpeculativeModeAsync(CancellationToken cancellationToken = default);

    /// <summary>The installed draft-model name for <c>draft-*</c> modes, or <see langword="null" /> when unset.</summary>
    Task<string?> GetSpeculativeDraftModelNameAsync(CancellationToken cancellationToken = default);

    /// <summary>The draft tokens per step <c>--spec-draft-n-max</c> (stored &gt; seed 3; <c>0</c> omits the flag).</summary>
    Task<int> GetSpeculativeDraftMaxTokensAsync(CancellationToken cancellationToken = default);

    /// <summary>The draft-model GPU layers <c>--spec-draft-ngl</c>, or <see langword="null" /> when unset (flag omitted).</summary>
    Task<int?> GetSpeculativeDraftGpuLayersAsync(CancellationToken cancellationToken = default);

    // Synchronous twins for the composition/startup path only (DI factory seeds + singleton constructors). These read
    // the stored settings synchronously to avoid blocking on async file I/O during host startup, which starves the
    // thread pool. Request-time consumers must use the async getters above.

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

    /// <inheritdoc cref="GetChatCacheReuseAsync" />
    int GetChatCacheReuse();

    /// <inheritdoc cref="GetSpeculativeModeAsync" />
    string GetSpeculativeMode();

    /// <inheritdoc cref="GetSpeculativeDraftModelNameAsync" />
    string? GetSpeculativeDraftModelName();

    /// <inheritdoc cref="GetSpeculativeDraftMaxTokensAsync" />
    int GetSpeculativeDraftMaxTokens();

    /// <inheritdoc cref="GetSpeculativeDraftGpuLayersAsync" />
    int? GetSpeculativeDraftGpuLayers();
}
