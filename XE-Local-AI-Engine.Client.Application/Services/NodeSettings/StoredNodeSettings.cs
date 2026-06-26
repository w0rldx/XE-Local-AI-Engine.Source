namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Client.Models;

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
}
