namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     The persisted, user-editable subset of node runtime settings. Every field beyond the original
///     <see cref="MaxMessageRequestTimeoutSeconds" /> / <see cref="DefaultModelName" /> pair is nullable so an older
///     <c>node-settings.json</c> written before a field existed deserializes to <see langword="null" /> and is then
///     backfilled from the appsettings seed by <c>INodeRuntimeSettings</c> (precedence stored &gt; seed &gt; default).
///     <c>NodeSettingsStore.Normalize</c> clamps/validates each field; an out-of-range stored value falls back to
///     <see langword="null" /> (re-seeded) rather than throwing.
/// </summary>
public sealed partial record StoredNodeSettings
{
    public const int DefaultMaxMessageRequestTimeoutSeconds = 300;

    public const int MinMaxMessageRequestTimeoutSeconds = 5;

    public const int MaxMaxMessageRequestTimeoutSeconds = 3600;

    // Seed defaults for migrated fields. These mirror the appsettings/Options defaults at the time of authoring and
    // serve as the hardcoded fallback when neither a stored value nor an appsettings seed is available.
    public const bool DefaultEnableTools = true;

    public const string DefaultOllamaEndpoint = "http://127.0.0.1:11434";

    public const string DefaultHuggingFaceQuant = "Q4_K_M";

    public const long DefaultHuggingFaceDiskMarginBytes = 1L * 1024 * 1024 * 1024;

    public const int DefaultLlamaMaxLoadedProcesses = 3;

    public const int MinLlamaMaxLoadedProcesses = 1;

    public const int MaxLlamaMaxLoadedProcesses = 16;

    public const int DefaultLlamaIdleTimeToLiveSeconds = 900;

    public const int MinLlamaIdleTimeToLiveSeconds = 30;

    public const int MaxLlamaIdleTimeToLiveSeconds = 86400;

    public const int DefaultMaxResponseSizeMb = 10;

    public const int MinMaxResponseSizeMb = 1;

    public const int MaxMaxResponseSizeMb = 100;

    public const string DefaultRecommendedLlamaCppTag = "b9692";

    public const int DefaultOrchestrationIdleTimeoutSeconds = 120;

    public const int MinOrchestrationIdleTimeoutSeconds = 1;

    public const int MaxOrchestrationIdleTimeoutSeconds = 3600;

    public const int DefaultAgentHomePrepareTimeoutSeconds = 900;

    public const int DefaultAgentHomeCommandTimeoutSeconds = 300;

    public const int MinAgentHomeTimeoutSeconds = 1;

    public const int MaxAgentHomeTimeoutSeconds = 86400;

    public const long DefaultAgentHomeMaxSelectedFolderBytes = 536870912;

    public const long DefaultAgentHomeMaxPatchBytes = 52428800;

    public const int DefaultMaxPendingToolCallAgeMinutes = 10;

    public const int MinMaxPendingToolCallAgeMinutes = 1;

    public const int MaxMaxPendingToolCallAgeMinutes = 60;

    /// <summary>Default chat-role <c>--cache-reuse</c> window (tokens); mirrors <c>LlamaServerSupervisorOptions.ChatCacheReuse</c>.</summary>
    public const int DefaultChatCacheReuse = 256;

    /// <summary><c>0</c> disables prompt-cache prefix reuse (upstream default).</summary>
    public const int MinChatCacheReuse = 0;

    /// <summary>Upper guard for the cache-reuse window; larger values are clamped away as almost certainly a mistake.</summary>
    public const int MaxChatCacheReuse = 8192;

    /// <summary>Default <c>--spec-type</c> — speculative decoding off (operator opt-in). Mirrors <c>SpeculativeDecodingSettings.DisabledMode</c>.</summary>
    public const string DefaultSpeculativeMode = SpeculativeDecodingSettings.DisabledMode;

    /// <summary>Default draft tokens per step (<c>--spec-draft-n-max</c>); mirrors <c>LlamaServerSupervisorOptions.SpeculativeDraftMaxTokens</c>.</summary>
    public const int DefaultSpeculativeDraftMaxTokens = 3;

    /// <summary><c>0</c> omits the <c>--spec-draft-n-max</c> flag (runtime default drafting).</summary>
    public const int MinSpeculativeDraftMaxTokens = 0;

    /// <summary>Upper guard for draft tokens per step.</summary>
    public const int MaxSpeculativeDraftMaxTokens = 16;

    /// <summary><c>0</c> offloads no draft-model layers to the GPU (<c>--spec-draft-ngl</c>).</summary>
    public const int MinSpeculativeDraftGpuLayers = 0;

    /// <summary>Upper guard for draft-model GPU layers (well above any real model's layer count).</summary>
    public const int MaxSpeculativeDraftGpuLayers = 1000;

    /// <summary>Node-level master flag for the client voice (TTS) feature. Default (absent) is off.</summary>
    public const bool DefaultVoiceFeatureEnabled = false;

    /// <summary>
    ///     The canonical Kokoro ONNX model id the client may load by default. Mirrors the authoritative metadata source
    ///     <c>KokoroVoiceCatalog.ModelId</c> (kept as a literal here to keep the settings record free of a runtime-catalog
    ///     dependency).
    /// </summary>
    public const string DefaultVoiceModelId = "onnx-community/Kokoro-82M-v1.0-ONNX";

    /// <summary>The default Kokoro voice profile id when none is stored.</summary>
    public const string DefaultVoiceProfileId = "af_heart";

    /// <summary>The default allow-list of voice model ids the client may load (just the bundled Kokoro model).</summary>
    public static readonly IReadOnlyList<string> DefaultAllowedVoiceModels = [DefaultVoiceModelId];

    /// <summary>Tag format gate: a llama.cpp release tag is a literal <c>b</c> followed by one or more digits.</summary>
    public const string RecommendedLlamaCppTagPattern = "^b[0-9]+$";

    [GeneratedRegex(RecommendedLlamaCppTagPattern, RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex RecommendedTagRegex();

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="tag" /> matches the pinned-tag format (<c>b</c>+digits).
    /// </summary>
    public static bool IsValidRecommendedLlamaCppTag(string? tag)
    {
        return !string.IsNullOrWhiteSpace(tag) && RecommendedTagRegex().IsMatch(tag);
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="mode" /> is a recognized <c>--spec-type</c> value (or
    ///     empty/<c>none</c>, i.e. disabled). Delegates to <see cref="SpeculativeDecodingSettings.IsAllowedMode" /> so the
    ///     accepted set has one authority (the pinned-build-verified list in the provider).
    /// </summary>
    public static bool IsValidSpeculativeMode(string? mode)
    {
        return SpeculativeDecodingSettings.IsAllowedMode(mode);
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="mode" /> is a <c>draft-*</c> speculative mode that REQUIRES
    ///     a draft model. Delegates to <see cref="SpeculativeDecodingSettings.ModeRequiresDraftModel" /> so the boundary
    ///     validator + save-endpoint cross-field guard share one authority for the draft-family test.
    /// </summary>
    public static bool SpeculativeModeRequiresDraftModel(string? mode)
    {
        return SpeculativeDecodingSettings.ModeRequiresDraftModel(mode);
    }

    public int MaxMessageRequestTimeoutSeconds { get; init; } = DefaultMaxMessageRequestTimeoutSeconds;

    /// <summary>
    ///     Canonical home for the local-chat default model. Reconciles the migrated <c>Agent:LocalChat:DefaultModel</c>:
    ///     the store is authoritative; appsettings only seeds this when it is <see langword="null" /> on first run.
    /// </summary>
    public string? DefaultModelName { get; init; }

    /// <summary>Whether the local-chat offer list includes executable tools by default. Seed: <c>Agent:LocalChat:EnableTools</c>.</summary>
    public bool? EnableTools { get; init; }

    /// <summary>The AgentHome tool-capable model allowlist. Seed: <c>AgentHome:ToolCapableModels</c>.</summary>
    public IReadOnlyList<string>? ToolCapableModels { get; init; }

    /// <summary>The Ollama runtime endpoint. Seed: <c>Ollama:Endpoint</c>. Applies after restart (read at host build).</summary>
    public string? OllamaEndpoint { get; init; }

    /// <summary>The Hugging Face default quant. Seed: <c>HuggingFace:DefaultQuant</c>.</summary>
    public string? HuggingFaceDefaultQuant { get; init; }

    /// <summary>Hugging Face disk-guard safety margin in bytes (developer-only). Seed: <c>HuggingFace:DiskMarginBytes</c>.</summary>
    public long? HuggingFaceDiskMarginBytes { get; init; }

    /// <summary>Max concurrently-loaded llama.cpp processes before spawn rejects. Seed: 3.</summary>
    public int? LlamaMaxLoadedProcesses { get; init; }

    /// <summary>Idle TTL (seconds) after which an unused llama.cpp process is reaped. Seed: 900.</summary>
    public int? LlamaIdleTimeToLiveSeconds { get; init; }

    /// <summary>Worker response-size cap in MiB. Seed: <c>WorkerNode:MaxResponseSizeMb</c>.</summary>
    public int? MaxResponseSizeMb { get; init; }

    /// <summary>The recommended llama.cpp release tag. Seed: <c>LlamaCppReleasePins.PinnedTag</c> ("b9692").</summary>
    public string? RecommendedLlamaCppTag { get; init; }

    /// <summary>Orchestration idle-timeout (seconds, developer-only). Seed: <c>Agent:Orchestration:IdleTimeoutSeconds</c> (120).</summary>
    public int? OrchestrationIdleTimeoutSeconds { get; init; }

    /// <summary>AgentHome prepare-phase timeout (seconds, developer-only). Seed: <c>AgentHome:PrepareTimeoutSeconds</c> (900).</summary>
    public int? AgentHomePrepareTimeoutSeconds { get; init; }

    /// <summary>AgentHome per-command timeout (seconds, developer-only). Seed: <c>AgentHome:CommandTimeoutSeconds</c> (300).</summary>
    public int? AgentHomeCommandTimeoutSeconds { get; init; }

    /// <summary>AgentHome per-folder byte budget (developer-only). Seed: <c>AgentHome:MaxSelectedFolderBytes</c>.</summary>
    public long? AgentHomeMaxSelectedFolderBytes { get; init; }

    /// <summary>AgentHome exported-patch byte budget (developer-only). Seed: <c>AgentHome:MaxPatchBytes</c>.</summary>
    public long? AgentHomeMaxPatchBytes { get; init; }

    /// <summary>Pending tool-call max age (minutes, developer-only). Seed: <c>WorkerNode:MaxPendingToolCallAgeMinutes</c> (10).</summary>
    public int? MaxPendingToolCallAgeMinutes { get; init; }

    /// <summary>
    ///     Chat-role prompt-cache prefix-reuse window in tokens (<c>--cache-reuse</c>). Seed: 256; <c>0</c> disables.
    ///     Applies on the next node restart (seeded into the supervisor options at host build).
    /// </summary>
    public int? ChatCacheReuse { get; init; }

    /// <summary>
    ///     Chat-role speculative-decoding <c>--spec-type</c> (e.g. <c>ngram-mod</c>, <c>draft-simple</c>). Seed:
    ///     <c>none</c> (off). Out-of-range/unknown falls back to <see langword="null" /> (re-seeded to disabled). Applies
    ///     on the next node restart.
    /// </summary>
    public string? SpeculativeMode { get; init; }

    /// <summary>
    ///     Installed draft-model NAME for <c>draft-*</c> speculative modes, resolved server-side to its GGUF path on the
    ///     spawn path (like the target model). Ignored by <c>ngram-*</c> modes. Applies on the next node restart.
    /// </summary>
    public string? SpeculativeDraftModelName { get; init; }

    /// <summary>Draft tokens proposed per step (<c>--spec-draft-n-max</c>). Seed: 3; <c>0</c> omits the flag.</summary>
    public int? SpeculativeDraftMaxTokens { get; init; }

    /// <summary>Draft-model GPU layers to offload (<c>--spec-draft-ngl</c>). <see langword="null" /> omits the flag.</summary>
    public int? SpeculativeDraftGpuLayers { get; init; }

    /// <summary>
    ///     Installed cross-encoder reranker model NAME for the knowledge-base search rerank stage
    ///     (<c>KnowledgeBaseOptions.RerankerModelName</c>). <see langword="null" />/blank (default) leaves reranking OFF;
    ///     a value enables it, resolved server-side to a rerank-role llama-server on the search path. Applies on the next
    ///     node restart (seeded into the knowledge-base options at host build).
    /// </summary>
    public string? RerankerModelName { get; init; }

    /// <summary>
    ///     Node-level sampling defaults (developer-only, optional). <see langword="null" /> = no node-level override —
    ///     today's behavior. Persisting the shape is done; consumption on the loopback send path is a follow-up.
    /// </summary>
    public SamplingOptions? SamplingDefaults { get; init; }

    /// <summary>
    ///     Node-level master flag for the client voice (TTS) feature. <see langword="null" /> (absent) reads as
    ///     <see cref="DefaultVoiceFeatureEnabled" /> (off). The voice manifest endpoint surfaces this as <c>Enabled</c>.
    /// </summary>
    public bool? VoiceFeatureEnabled { get; init; }

    /// <summary>
    ///     Allow-list of voice model ids the client may load. <see langword="null" /> (absent) reads as
    ///     <see cref="DefaultAllowedVoiceModels" /> (just the bundled Kokoro model).
    /// </summary>
    public IReadOnlyList<string>? AllowedVoiceModels { get; init; }

    /// <summary>
    ///     The default Kokoro voice profile id offered to the client. <see langword="null" /> (absent) reads as
    ///     <see cref="DefaultVoiceProfileId" /> (<c>af_heart</c>).
    /// </summary>
    public string? DefaultVoiceProfile { get; init; }

    /// <summary>
    ///     Node-default tool-approval policy (OPP-03). <see langword="null" /> (absent, the default) means no node-level
    ///     tightening — the resolver keeps each tool's own catalog approval flag, byte-identical to the pre-feature path.
    ///     A value can only ADD an approval requirement (tighten-only, composed on top of the catalog default); it can
    ///     never waive one. Applies on the next node restart (read once at composition).
    /// </summary>
    public NodeToolApprovalPolicySettings? ToolApprovalPolicy { get; init; }

    /// <summary>
    ///     Operator override of usage cost rates. <see langword="null" /> (absent, the default) means no
    ///     override — the usage-summary cost estimate uses the built-in default rate table, and any model with neither an
    ///     override nor a default is unpriced (zero). A value supplies per-model-name USD rates that win over the defaults;
    ///     local runtimes stay free regardless. Negative / non-finite entries are dropped by
    ///     <c>NodeSettingsStore.Normalize</c> on read. Applies on the next usage-summary read (the cost resolver reads
    ///     current node settings; no restart needed).
    /// </summary>
    public NodeUsageRateSettings? UsageRates { get; init; }

    /// <summary>
    ///     Stable, LOCAL-ONLY machine identifier used to key inference profiles to the box they were tuned on. Generated
    ///     once (<see cref="System.Guid.NewGuid" />, <c>"N"</c> format) by <c>IMachineKeyProvider</c> on first use and
    ///     persisted here; <see langword="null" /> until then (it is generated, not seeded — there is no appsettings
    ///     default). NEVER emitted in telemetry, aggregates, or logs.
    /// </summary>
    public string? MachineKey { get; init; }
}
