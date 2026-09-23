namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>The persisted, user-editable subset of node runtime settings.</summary>
/// <remarks>
///     Every field beyond the original <see cref="MaxMessageRequestTimeoutSeconds" /> /
///     <see cref="DefaultModelName" /> pair is nullable, so a <c>node-settings.json</c> written before a field existed
///     deserializes to <see langword="null" /> and is then backfilled from the appsettings seed by
///     <c>INodeRuntimeSettings</c> (precedence stored &gt; seed &gt; default). <c>NodeSettingsStore.Normalize</c>
///     clamps/validates each field; an out-of-range stored value falls back to <see langword="null" /> (re-seeded).
/// </remarks>
public sealed partial record StoredNodeSettings
{
    public const int DefaultMaxMessageRequestTimeoutSeconds = 600;

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

    /// <summary>Default idle time-to-live for the whisper.cpp transcription daemon, in minutes.</summary>
    public const int DefaultTranscriptionIdleTimeoutMinutes = 15;

    /// <summary>Lower clamp for <see cref="TranscriptionIdleTimeoutMinutes" />.</summary>
    public const int MinTranscriptionIdleTimeoutMinutes = 1;

    /// <summary>Upper clamp for <see cref="TranscriptionIdleTimeoutMinutes" />.</summary>
    public const int MaxTranscriptionIdleTimeoutMinutes = 240;

    /// <summary>Keep-model-warm is opt-in; an absent stored value stays off.</summary>
    public const bool DefaultKeepModelWarmEnabled = false;

    /// <summary>Default cadence for refreshing the selected model's idle timestamp.</summary>
    public const int DefaultKeepModelWarmIntervalSeconds = 300;

    /// <summary>Smallest supported keep-warm cadence; matches the background service's live-settings poll interval.</summary>
    public const int MinKeepModelWarmIntervalSeconds = 5;

    /// <summary>Largest supported keep-warm cadence. It must still remain below the configured llama.cpp idle TTL.</summary>
    public const int MaxKeepModelWarmIntervalSeconds = 3600;

    public const int DefaultMaxResponseSizeMb = 10;

    public const int MinMaxResponseSizeMb = 1;

    public const int MaxMaxResponseSizeMb = 100;

    /// <summary>
    ///     The llama.cpp release tag the UI shows as "Recommended", ALIASED to
    ///     <see cref="LlamaCppReleasePins.PinnedTag" /> and never re-literalled.
    /// </summary>
    /// <remarks>
    ///     As an independent string literal it had to be bumped in lock-step by hand, and went 509 builds stale while
    ///     the engine's own pin had moved. The layering permits the reference: the frozen direction forbids a PROVIDER
    ///     depending on Client/Application, not the reverse, and this assembly already references
    ///     <c>Providers.LlamaServer</c>. Const-to-const, so it still inlines as a compile-time constant and stays
    ///     usable in attributes and switch patterns.
    /// </remarks>
    public const string DefaultRecommendedLlamaCppTag = LlamaCppReleasePins.PinnedTag;

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

    /// <summary>Default grace, in seconds, before a run whose last client disconnected is cancelled.</summary>
    /// <remarks>
    ///     Generous on purpose: the clock starts when the STREAM tears down, so it must comfortably exceed the
    ///     client's automatic-reconnect window — a resource-only argument would suggest 30–60 s.
    /// </remarks>
    public const int DefaultDetachedGraceSeconds = 300;

    /// <summary><c>0</c> disables the disconnect grace entirely: a detached run is bounded only by the whole-invocation watchdog.</summary>
    public const int MinDetachedGraceSeconds = 0;

    /// <summary>Upper guard for the disconnect grace (24 h); above this the knob is indistinguishable from disabling it.</summary>
    public const int MaxDetachedGraceSeconds = 86400;

    /// <summary>Default chat-role <c>--cache-reuse</c> window (tokens); mirrors <c>LlamaServerSupervisorOptions.ChatCacheReuse</c>.</summary>
    public const int DefaultChatCacheReuse = 256;

    /// <summary><c>0</c> disables prompt-cache prefix reuse (upstream default).</summary>
    public const int MinChatCacheReuse = 0;

    /// <summary>Upper guard for the cache-reuse window; larger values are clamped away as almost certainly a mistake.</summary>
    public const int MaxChatCacheReuse = 8192;

    /// <summary>Default <c>--spec-type</c> — speculative decoding off (operator opt-in). Mirrors <c>SpeculativeDecodingSettings.DisabledMode</c>.</summary>
    public const string DefaultSpeculativeMode = SpeculativeDecodingSettings.DisabledMode;

    /// <summary>
    ///     Default KV-cache type for GPU chat spawns; mirrors <c>LlamaServerLaunchPolicyOptions.KvCacheType</c>'s own
    ///     default.
    /// </summary>
    /// <remarks>
    ///     An unset setting therefore seeds an options object equal to the provider default, so the launch argv, the
    ///     launch identity and the inference-profile fingerprint are all byte-identical to a node that never had this
    ///     knob.
    /// </remarks>
    public const string DefaultKvCacheType = LlamaServerKvCacheTypes.Q8_0;

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

    /// <summary>Node kill-switch for the user-defined custom tools feature; default (absent) is OFF.</summary>
    /// <remarks>
    ///     Custom tools execute host processes / outbound fetches, so the whole feature is opt-in at the node level
    ///     (the per-agent allow-list and the forced per-call approval remain the second and third gates). When off, no
    ///     custom tool is OFFERED to any model and <c>ICustomToolCatalog.TryResolveManyAsync</c> refuses to resolve one.
    /// </remarks>
    public const bool DefaultCustomToolsEnabled = false;

    /// <summary>
    ///     Node switch for the per-turn tool-relevance offer. Default (absent) is OFF: the filter stays a
    ///     pass-through, so every offer is byte-identical to the pre-toggle behaviour.
    /// </summary>
    public const bool DefaultToolRelevanceEnabled = false;

    /// <summary>
    ///     Default application-container runtime selection. <c>auto</c> lets the engine pick, which in this version is
    ///     always Docker; the constant exists so a reader of an absent setting is told what absent means.
    /// </summary>
    public const string DefaultContainerRuntimeSelection = ContainerRuntimeSelectionParser.Auto;

    /// <summary>The <see cref="ExternalAccessProfile" /> literal recording that the recommended preset is in force.</summary>
    public const string ExternalAccessProfileRecommended = "recommended";

    /// <summary>The <see cref="ExternalAccessProfile" /> literal recording that the offline / manual preset is in force.</summary>
    public const string ExternalAccessProfileOffline = "offline";

    /// <summary>
    ///     The <see cref="ExternalAccessProfile" /> literal the save mapper stamps when the three switches no longer match
    ///     either preset. Engine-written: a client never computes or sends it.
    /// </summary>
    public const string ExternalAccessProfileCustom = "custom";

    /// <summary>
    ///     The <see cref="ExternalAccessProfile" /> literal first-run setup writes once the administrator exists and before
    ///     the operator has chosen a preset. Engine-written: a client never sends it, and the boundary validator rejects it.
    /// </summary>
    public const string ExternalAccessProfilePending = "pending";

    /// <summary>The <see cref="UiMode" /> literal for the reduced navigation: the everyday surfaces only.</summary>
    public const string UiModeSimple = "simple";

    /// <summary>The <see cref="UiMode" /> literal for the full navigation — every entry this build offers.</summary>
    public const string UiModeAdvanced = "advanced";

    /// <summary>
    ///     What an absent <see cref="UiMode" /> reads as. <c>advanced</c> so a node that has never answered the question
    ///     shows exactly the navigation it showed before the mode existed.
    /// </summary>
    public const string DefaultUiMode = UiModeAdvanced;

    /// <summary>
    ///     Automatic application-update checks are ON when unset, so an upgraded node behaves exactly as it did before this
    ///     switch existed. Gates <c>AppUpdateCheckService</c> only; the manual check and apply flow ignore it.
    /// </summary>
    public const bool DefaultAutoCheckApplicationUpdates = true;

    /// <summary>
    ///     Automatic llama.cpp / runtime update checks are ON when unset. Gates <c>LlamaCppUpdateCheckService</c> only; the
    ///     manual runtime-status refresh and the runtime install ignore it.
    /// </summary>
    public const bool DefaultAutoCheckRuntimeUpdates = true;

    /// <summary>
    ///     First-run model provisioning is ON when unset. Gates <c>FirstRunModelProvisioningService</c> only; a manual model
    ///     download or install ignores it. It sits AFTER the existing <c>FirstRunModel:Enabled</c> config gate, not instead of it.
    /// </summary>
    public const bool DefaultAutoProvisionFirstRunModel = true;

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
    ///     Returns <see langword="true" /> when <paramref name="mode" /> is an EXTERNAL-DRAFT speculative mode — one
    ///     that runs a second GGUF and so REQUIRES a draft model.
    /// </summary>
    /// <remarks>
    ///     <c>draft-mtp</c> drafts from heads inside the main model and is false here despite the name prefix.
    ///     Delegates to <see cref="SpeculativeDecodingSettings.ModeRequiresDraftModel" /> so the boundary validator and
    ///     the save-endpoint cross-field guard share one authority for the classification.
    /// </remarks>
    public static bool SpeculativeModeRequiresDraftModel(string? mode)
    {
        return SpeculativeDecodingSettings.ModeRequiresDraftModel(mode);
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="value" /> names a container runtime this engine knows
    ///     (<c>auto</c> or <c>docker</c>, ordinal-ignore-case). Delegates to
    ///     <see cref="ContainerRuntimeSelectionParser.TryParse" /> so the stored, wire and engine representations of the
    ///     selection have one authority rather than an allow-list restated per caller.
    /// </summary>
    public static bool IsValidContainerRuntimeSelection(string? value)
    {
        return ContainerRuntimeSelectionParser.TryParse(value, out _);
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="type" /> is a recognized KV-cache type (or empty, i.e.
    ///     "use the node default"). Delegates to <see cref="LlamaServerKvCacheTypes.IsAllowed" /> so the allow-list has
    ///     one authority shared with the benchmark KV picker.
    /// </summary>
    public static bool IsValidKvCacheType(string? type)
    {
        return LlamaServerKvCacheTypes.IsAllowed(type);
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="profile" /> is one of the four PERSISTABLE
    ///     external-access literals: <see cref="ExternalAccessProfileRecommended" />,
    ///     <see cref="ExternalAccessProfileOffline" />, <see cref="ExternalAccessProfileCustom" />,
    ///     <see cref="ExternalAccessProfilePending" />.
    /// </summary>
    /// <remarks>
    ///     This is what may be STORED, so <c>NodeSettingsStore.Normalize</c> uses it. The comparison is ordinal (a
    ///     constant string pattern), so <c>"Offline"</c> is rejected rather than silently accepted.
    ///     <see langword="null" /> is a state (undecided), not a literal, and is <see langword="false" /> here.
    /// </remarks>
    public static bool IsValidExternalAccessProfile(string? profile)
    {
        return profile is ExternalAccessProfileRecommended
            or ExternalAccessProfileOffline
            or ExternalAccessProfileCustom
            or ExternalAccessProfilePending;
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="profile" /> is a preset a CLIENT may send:
    ///     <see cref="ExternalAccessProfileRecommended" /> or <see cref="ExternalAccessProfileOffline" />.
    ///     <see cref="ExternalAccessProfileCustom" /> and <see cref="ExternalAccessProfilePending" /> are engine-written
    ///     states — the boundary validator rejects them as inputs and the save mapper is their only writer.
    /// </summary>
    public static bool IsExternalAccessPreset(string? profile)
    {
        return profile is ExternalAccessProfileRecommended or ExternalAccessProfileOffline;
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="mode" /> is one of the two navigation modes,
    ///     <see cref="UiModeSimple" /> or <see cref="UiModeAdvanced" />.
    /// </summary>
    /// <remarks>
    ///     The comparison is ordinal (a constant string pattern), so <c>"Simple"</c> is rejected rather than silently
    ///     accepted. <see langword="null" /> is a state (not answered yet), not a literal, and is
    ///     <see langword="false" /> here. Unlike the external-access profile there is no engine-written third literal:
    ///     the client may send either value it may store.
    /// </remarks>
    public static bool IsValidUiMode(string? mode)
    {
        return mode is UiModeSimple or UiModeAdvanced;
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="channel" /> is one of the three update-channel
    ///     literals: <c>stable</c>, <c>preview</c> or <c>development</c>.
    /// </summary>
    /// <remarks>
    ///     Delegates to <see cref="AppUpdateChannelNames.TryParse" /> so the literals are declared once, not re-listed
    ///     here. The comparison is ordinal, so <c>"Stable"</c> is rejected rather than silently accepted.
    ///     <see langword="null" /> is a state (never chosen), not a literal, and is <see langword="false" /> here.
    /// </remarks>
    public static bool IsValidUpdateChannel(string? channel)
    {
        return AppUpdateChannelNames.TryParse(channel, out _);
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

    /// <summary>
    ///     Whether the selected local chat model is periodically touched so the runtime keeps it resident. Absent reads
    ///     as <see cref="DefaultKeepModelWarmEnabled" /> (off).
    /// </summary>
    public bool? KeepModelWarmEnabled { get; init; }

    /// <summary>The installed local chat model name to keep resident. Blank values normalize to <see langword="null" />.</summary>
    public string? KeepModelWarmModelName { get; init; }

    /// <summary>
    ///     Seconds between keep-warm touches. Seed: 300. The value must remain below the active llama.cpp idle TTL to
    ///     prevent eviction.
    /// </summary>
    public int? KeepModelWarmIntervalSeconds { get; init; }

    /// <summary>Worker response-size cap in MiB. Seed: <c>WorkerNode:MaxResponseSizeMb</c>.</summary>
    public int? MaxResponseSizeMb { get; init; }

    /// <summary>The recommended llama.cpp release tag. Seed: <c>LlamaCppReleasePins.PinnedTag</c> ("b10201").</summary>
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
    ///     How long a run whose last client disconnected keeps going before it is cancelled, in seconds. Seed:
    ///     <c>WorkerNode:DetachedGraceSeconds</c> (300). <c>0</c> means never cancel — today's behavior. Applies on the
    ///     next reaper tick (read per tick, not cached).
    /// </summary>
    public int? DetachedGraceSeconds { get; init; }

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
    ///     KV-cache element type for GPU chat spawns (<c>-ctk</c>/<c>-ctv</c>): <c>f16</c> | <c>q8_0</c> | <c>q4_0</c>.
    ///     Seed: <c>q8_0</c>; <c>f16</c> emits no KV or flash-attention flags at all.
    /// </summary>
    /// <remarks>
    ///     Unknown falls back to <see langword="null" /> (re-seeded to the default). Applies on the next node restart,
    ///     and CHANGING IT invalidates every frozen inference profile on this node — the selected type is part of the
    ///     launch-policy fingerprint, so each model re-explores under the new type before it can replay again.
    /// </remarks>
    public string? KvCacheType { get; init; }

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
    ///     (<c>KnowledgeBaseOptions.RerankerModelName</c>).
    /// </summary>
    /// <remarks>
    ///     <see langword="null" />/blank (default) leaves reranking OFF; a value enables it, resolved server-side to a
    ///     rerank-role llama-server on the search path. Applies on the next node restart (seeded into the
    ///     knowledge-base options at host build).
    /// </remarks>
    public string? RerankerModelName { get; init; }

    /// <summary>
    ///     Installed node-local chat model the reasoning-effort dispatcher moves a FAST <c>auto</c> turn onto.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" />/blank (default) leaves the swap OFF — an <c>auto</c> turn then keeps its model and
    ///     only lowers the effort. It is validated at save to be an installed llama.cpp model (never a cloud id, an
    ///     external id, or an Ollama name) and to leave a second loaded-process slot, and re-validated per turn.
    ///     NOT restart-gated: it is read per send, so a save applies to the next turn.
    /// </remarks>
    public string? AutoEffortFastModelName { get; init; }

    /// <summary>
    ///     Node-level master flag for the client voice (TTS) feature. <see langword="null" /> (absent) reads as
    ///     <see cref="DefaultVoiceFeatureEnabled" /> (off).
    /// </summary>
    public bool? VoiceFeatureEnabled { get; init; }

    /// <summary>
    ///     Node kill-switch for the user-defined custom tools feature. <see langword="null" /> (absent) reads as
    ///     <see cref="DefaultCustomToolsEnabled" /> (off). A bool needs no clamping, so <c>NodeSettingsStore.Normalize</c>
    ///     passes it through untouched.
    /// </summary>
    public bool? CustomToolsEnabled { get; init; }

    /// <summary>
    ///     Node switch for the per-turn tool-relevance offer. <see langword="null" /> (absent) reads as
    ///     <see cref="DefaultToolRelevanceEnabled" /> (off). A bool needs no clamping, so <c>NodeSettingsStore.Normalize</c>
    ///     passes it through untouched.
    /// </summary>
    public bool? ToolRelevanceEnabled { get; init; }

    /// <summary>
    ///     Which external-access preset was last applied: a RECORD of the choice, never the authority.
    /// </summary>
    /// <remarks>
    ///     Every gate reads the three booleans below; this member is read for exactly one purpose — telling a decided node from an undecided one.
    ///     <see langword="null" /> means nobody has decided AND no administrator exists (a fresh boot), or an upgraded node not yet backfilled.
    ///     <see cref="ExternalAccessProfilePending" /> means an administrator exists and the choice has not been made, recommended/offline that a
    ///     preset is in force, and custom that the switches no longer match either preset. Both <see langword="null" /> and pending are UNDECIDED
    ///     to the gated services; only <see langword="null" /> is backfillable.
    /// </remarks>
    public string? ExternalAccessProfile { get; init; }

    /// <summary>
    ///     Which navigation mode the operator chose: <see cref="UiModeSimple" /> or <see cref="UiModeAdvanced" />.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" /> means the question has not been answered, which is what the SPA's first-run step keys on;
    ///     <c>UiModeBackfillService</c> stamps <see cref="UiModeAdvanced" /> at boot on a node that finished onboarding before
    ///     this setting existed, so an upgraded node is never asked. PRESENTATION ONLY, and never a security boundary: it
    ///     decides which navigation entries are rendered and nothing else. Every route stays reachable by URL, no server gate
    ///     reads it, and the compile-time capability flags still decide what exists at all.
    /// </remarks>
    public string? UiMode { get; init; }

    /// <summary>
    ///     Which application-update channel this node follows: <c>stable</c>, <c>preview</c> or <c>development</c>.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" /> means the operator has never chosen, and reads as the channel baked into this
    ///     artifact (<c>AppUpdateChannelOptions.DefaultChannel</c>) — so an upgraded node keeps exactly the update
    ///     visibility its flavour always had. The backend is the only authority: the SPA renders what the status
    ///     endpoint reports and never derives eligibility itself.
    /// </remarks>
    public string? UpdateChannel { get; init; }

    /// <summary>Whether the node checks for application updates on its own.</summary>
    /// <remarks>
    ///     <see langword="null" /> (absent) reads as <see cref="DefaultAutoCheckApplicationUpdates" /> (on), so an
    ///     upgraded node keeps today's behaviour. A bool needs no clamping, so <c>NodeSettingsStore.Normalize</c>
    ///     passes it through untouched. The manual check and apply flow never consult it.
    /// </remarks>
    public bool? AutoCheckApplicationUpdates { get; init; }

    /// <summary>Whether the node checks for llama.cpp / runtime updates on its own.</summary>
    /// <remarks>
    ///     <see langword="null" /> (absent) reads as <see cref="DefaultAutoCheckRuntimeUpdates" /> (on). A bool needs
    ///     no clamping, so <c>NodeSettingsStore.Normalize</c> passes it through untouched. The manual runtime-status
    ///     refresh and the runtime install never consult it.
    /// </remarks>
    public bool? AutoCheckRuntimeUpdates { get; init; }

    /// <summary>
    ///     Whether the node downloads a first-run model and runtime on its own. <see langword="null" /> (absent) reads as
    ///     <see cref="DefaultAutoProvisionFirstRunModel" /> (on). A bool needs no clamping, so
    ///     <c>NodeSettingsStore.Normalize</c> passes it through untouched. A manual model download or install never consults
    ///     it.
    /// </summary>
    public bool? AutoProvisionFirstRunModel { get; init; }

    /// <summary>
    ///     Preferred browser voice identifier. Older values such as <c>af_heart</c> remain valid persisted data; when
    ///     they do not identify an installed Web Speech voice the browser chooses its language/default voice instead.
    /// </summary>
    public string? DefaultVoiceProfile { get; init; }

    /// <summary>
    ///     Node-default tool-approval policy; <see langword="null" /> (absent, the default) means no node-level
    ///     tightening.
    /// </summary>
    /// <remarks>
    ///     Absent, the resolver keeps each tool's own catalog approval flag, byte-identical to the pre-feature path. A
    ///     value can only ADD an approval requirement (tighten-only, composed on top of the catalog default); it can
    ///     never waive one. Applies on the next node restart (read once at composition).
    /// </remarks>
    public NodeToolApprovalPolicySettings? ToolApprovalPolicy { get; init; }

    /// <summary>
    ///     Operator override of usage cost rates; <see langword="null" /> (absent, the default) means no override.
    /// </summary>
    /// <remarks>
    ///     Without an override the usage-summary cost estimate uses the built-in default rate table, and any model with
    ///     neither an override nor a default is unpriced (zero). A value supplies per-model-name USD rates that win over
    ///     the defaults; local runtimes stay free regardless. Negative / non-finite entries are dropped by
    ///     <c>NodeSettingsStore.Normalize</c> on read. Applies on the next usage-summary read (the cost resolver reads
    ///     current node settings, so no restart is needed).
    /// </remarks>
    public NodeUsageRateSettings? UsageRates { get; init; }

    /// <summary>
    ///     The operator's explicit whisper model choice; <see langword="null" /> (the default) means "use the hardware
    ///     recommendation", which is why an absent value is not a missing one.
    /// </summary>
    /// <remarks>
    ///     LOCAL-ONLY: deliberately absent from the node-settings wire DTO, so a save that maps a request onto a fresh
    ///     record must carry it over from the stored one or it is erased.
    /// </remarks>
    public string? TranscriptionSelectedModelId { get; init; }

    /// <summary>
    ///     Idle time-to-live for the whisper.cpp daemon, in minutes. <see langword="null" /> (absent) reads as
    ///     <see cref="DefaultTranscriptionIdleTimeoutMinutes" />; <c>NodeSettingsStore.Normalize</c> clamps to
    ///     <see cref="MinTranscriptionIdleTimeoutMinutes" />..<see cref="MaxTranscriptionIdleTimeoutMinutes" />.
    ///     Applies on the next node restart (read once when the runtime options are seeded).
    ///     <para>LOCAL-ONLY, exactly as <see cref="TranscriptionSelectedModelId" /> is.</para>
    /// </summary>
    public int? TranscriptionIdleTimeoutMinutes { get; init; }

    /// <summary>
    ///     Which container runtime application containers use: <c>auto</c> (the default) or <c>docker</c>.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" /> (absent) reads as <see cref="DefaultContainerRuntimeSelection" />, so a partial save that omits the field
    ///     preserves what is stored; unknown falls back to <see langword="null" /> in <c>NodeSettingsStore.Normalize</c>. Stored as a string
    ///     rather than as the engine's enum for the reason <c>SpeculativeMode</c> and <c>KvCacheType</c> are: this file is serialized with web
    ///     defaults and no enum converter, so an enum would persist as <c>0</c>/<c>1</c> in a file an operator hand-edits and would change
    ///     meaning silently if a value were ever inserted.
    /// </remarks>
    public string? ContainerRuntimeSelection { get; init; }

    /// <summary>
    ///     Stable, LOCAL-ONLY machine identifier used to key inference profiles to the box they were tuned on.
    /// </summary>
    /// <remarks>
    ///     Generated once (<see cref="System.Guid.NewGuid" />, <c>"N"</c> format) by <c>IMachineKeyProvider</c> on first
    ///     use and persisted here; <see langword="null" /> until then (it is generated, not seeded — there is no
    ///     appsettings default). NEVER emitted in telemetry, aggregates, or logs.
    /// </remarks>
    public string? MachineKey { get; init; }
}
