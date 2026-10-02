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

    public const long MinHuggingFaceDiskMarginBytes = 1;

    public const long MaxHuggingFaceDiskMarginBytes = 1024L * 1024 * 1024 * 1024;

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

    // Curated runtime tunables. Each Default is the value an absent setting runs with; each Min/Max pair is what Normalize
    // and the boundary validator enforce.

    /// <summary>Default llama-server readiness deadline cap, in seconds; mirrors <c>LlamaServerSupervisorOptions.ReadinessTimeoutCap</c>.</summary>
    public const int DefaultLlamaReadinessTimeoutCapSeconds = 600;

    /// <summary>
    ///     Lower bound for the readiness cap: the supervisor's fixed 120 s readiness BASE, because
    ///     <c>LlamaServerSupervisorOptions.Validate</c> rejects a cap below it at host build.
    /// </summary>
    public const int MinLlamaReadinessTimeoutCapSeconds = 120;

    public const int MaxLlamaReadinessTimeoutCapSeconds = 3600;

    /// <summary>Default chat-role HTTP timeout to a llama-server, in seconds; mirrors <c>LlamaServerSupervisorOptions.HttpNetworkTimeout</c>.</summary>
    public const int DefaultLlamaChatHttpTimeoutSeconds = 3600;

    public const int MinLlamaChatHttpTimeoutSeconds = 60;

    public const int MaxLlamaChatHttpTimeoutSeconds = 86400;

    /// <summary>Default embedding/rerank HTTP timeout, in seconds; mirrors <c>LlamaServerSupervisorOptions.EmbeddingHttpNetworkTimeout</c>.</summary>
    public const int DefaultLlamaEmbeddingHttpTimeoutSeconds = 600;

    public const int MinLlamaEmbeddingHttpTimeoutSeconds = 10;

    public const int MaxLlamaEmbeddingHttpTimeoutSeconds = 3600;

    /// <summary><c>0</c> disables the chat host prompt cache (<c>--cache-ram 0</c>).</summary>
    public const int MinLlamaChatCacheRamMiB = 0;

    public const int MaxLlamaChatCacheRamMiB = 131072;

    /// <summary>
    ///     The request-only sentinel that resets <see cref="LlamaChatCacheRamMiB" /> to automatic (stored
    ///     <see langword="null" />), because a <see langword="null" /> request member means "keep".
    /// </summary>
    public const int LlamaChatCacheRamMiBAuto = -1;

    /// <summary>Default CPU threads left free for the host; mirrors <c>LlamaServerLaunchPolicyOptions.CpuThreadReserve</c>.</summary>
    public const int DefaultLlamaCpuThreadReserve = 1;

    public const int MinLlamaCpuThreadReserve = 0;

    public const int MaxLlamaCpuThreadReserve = 64;

    /// <summary>Default share of GPU memory kept free, in percent; mirrors <c>LlamaServerLaunchPolicyOptions.DefaultGpuReserveFraction</c>.</summary>
    public const int DefaultLlamaGpuReservePercent = 5;

    public const int MinLlamaGpuReservePercent = 0;

    public const int MaxLlamaGpuReservePercent = 50;

    /// <summary>Default share of RAM kept free, in percent; mirrors <c>LlamaServerLaunchPolicyOptions.DefaultRamReserveFraction</c>.</summary>
    public const int DefaultLlamaRamReservePercent = 15;

    public const int MinLlamaRamReservePercent = 0;

    public const int MaxLlamaRamReservePercent = 75;

    /// <summary>Default sd-server idle time-to-live, in seconds; mirrors <c>StableDiffusionRuntimeOptions.IdleTimeToLive</c>.</summary>
    public const int DefaultImageIdleTimeToLiveSeconds = 900;

    public const int MinImageIdleTimeToLiveSeconds = 30;

    public const int MaxImageIdleTimeToLiveSeconds = 86400;

    /// <summary>Default model-fit safety margin, in percent; mirrors <c>MemoryFitEstimator.DefaultSafetyMarginFraction</c>.</summary>
    public const int DefaultModelFitSafetyMarginPercent = 12;

    public const int MinModelFitSafetyMarginPercent = 0;

    public const int MaxModelFitSafetyMarginPercent = 50;

    /// <summary>Hardcoded fallback for the provider-call ceiling; mirrors <c>ProviderCallBudgetOptions.MaxProviderCallsPerInvocation</c>.</summary>
    public const int DefaultMaxProviderCallsPerInvocation = 200;

    public const int MinMaxProviderCallsPerInvocation = 10;

    public const int MaxMaxProviderCallsPerInvocation = 2000;

    /// <summary>Default ceiling on a custom command tool's timeout, in seconds (the former <c>HostProcessExecutor.MaxTimeoutSeconds</c>).</summary>
    public const int DefaultCustomToolMaxTimeoutSeconds = 300;

    public const int MinCustomToolMaxTimeoutSeconds = 30;

    public const int MaxCustomToolMaxTimeoutSeconds = 3600;

    /// <summary>Default <c>web_fetch</c>/<c>web_search</c> time budget, in seconds (the former <c>WebFetchService.TimeBudget</c>).</summary>
    public const int DefaultWebFetchTimeoutSeconds = 20;

    public const int MinWebFetchTimeoutSeconds = 5;

    public const int MaxWebFetchTimeoutSeconds = 120;

    /// <summary>Default cap on the text <c>web_fetch</c> returns, in characters (the former <c>WebFetchService.MaxContentChars</c>).</summary>
    public const int DefaultWebFetchMaxContentChars = 12_000;

    public const int MinWebFetchMaxContentChars = 1000;

    public const int MaxWebFetchMaxContentChars = 100_000;

    /// <summary>Default <c>search_knowledge_base</c> hit count when the model names none.</summary>
    public const int DefaultKnowledgeSearchDefaultResults = 5;

    /// <summary>Default ceiling on the <c>search_knowledge_base</c> hit count.</summary>
    public const int DefaultKnowledgeSearchMaxResults = 20;

    public const int MinKnowledgeSearchResults = 1;

    /// <summary>
    ///     Upper bound for both knowledge-search counts: the tool's static JSON schema declares <c>"maximum": 20</c>, and
    ///     raising it is a tool-schema change (grammar smoke), so the setting can only lower the ceiling.
    /// </summary>
    public const int MaxKnowledgeSearchResults = 20;

    /// <summary>Fallback when the appsettings seed is unreadable; mirrors <c>HuggingFaceOptions.DownloadConnections</c>.</summary>
    public const int DefaultHuggingFaceDownloadConnections = 4;

    public const int MinHuggingFaceDownloadConnections = 1;

    /// <summary>The provider's own ceiling (<c>HfDownloadClient</c>): Hugging Face throttles per IP well before more streams pay.</summary>
    public const int MaxHuggingFaceDownloadConnections = 16;

    /// <summary>Default whisper.cpp per-request inference timeout, in minutes; mirrors <c>WhisperRuntimeOptions.InferenceTimeout</c>.</summary>
    public const int DefaultTranscriptionInferenceTimeoutMinutes = 30;

    public const int MinTranscriptionInferenceTimeoutMinutes = 1;

    public const int MaxTranscriptionInferenceTimeoutMinutes = 480;

    public const int MinAgentHomeMaxRunSeconds = 60;

    public const int MaxAgentHomeMaxRunSeconds = 86400;

    /// <summary>Hardcoded fallback for the run-folder retention; mirrors <c>AgentHomeRunRetentionOptions.RetentionDays</c>.</summary>
    public const int DefaultAgentHomeRunRetentionDays = 30;

    /// <summary>
    ///     A stored window is never 0, so the retention validator's "all three limits 0 while enabled" refusal stays unreachable from
    ///     node settings; only an appsettings seed of 0 with both stored caps at 0 can sweep nothing.
    /// </summary>
    public const int MinAgentHomeRunRetentionDays = 1;

    public const int MaxAgentHomeRunRetentionDays = 365;

    // Chat and agent-run knobs (round 2). Each Default mirrors its options class default, which the shipped appsettings.json does not
    // override; each Min/Max pair sits inside that options class's own validator bounds.

    /// <summary>Hardcoded fallback for the tool-loop ceiling; mirrors <c>AgentToolPipelineOptions.MaximumToolIterationsPerRequest</c>.</summary>
    public const int DefaultToolPipelineMaxIterationsPerRequest = 40;

    public const int MinToolPipelineMaxIterationsPerRequest = 1;

    public const int MaxToolPipelineMaxIterationsPerRequest = 200;

    /// <summary>Hardcoded fallback for the per-tool-result cap; mirrors <c>AgentToolPipelineOptions.MaxToolResultCharacters</c>.</summary>
    public const int DefaultToolPipelineMaxToolResultChars = 65_536;

    /// <summary>The <c>AgentToolPipelineOptionsValidator</c> floor.</summary>
    public const int MinToolPipelineMaxToolResultChars = 1024;

    public const int MaxToolPipelineMaxToolResultChars = 1_000_000;

    /// <summary>Hardcoded fallback; mirrors <c>AgentToolPipelineOptions.MaxConsecutiveInvalidToolCallsPerTool</c>.</summary>
    public const int DefaultToolPipelineMaxConsecutiveInvalidToolCalls = 3;

    public const int MinToolPipelineMaxConsecutiveInvalidToolCalls = 1;

    public const int MaxToolPipelineMaxConsecutiveInvalidToolCalls = 20;

    /// <summary>
    ///     Hardcoded fallback for the context window assumed when a send names none; mirrors both
    ///     <c>ProviderCallBudgetOptions.DefaultContextTokens</c> and <c>ConversationContextBudgetOptions.DefaultContextTokens</c>.
    /// </summary>
    public const int DefaultDefaultContextTokens = 8192;

    public const int MinDefaultContextTokens = 1024;

    public const int MaxDefaultContextTokens = 1_048_576;

    /// <summary>Hardcoded fallback; mirrors <c>ProviderCallBudgetOptions.RecentMessagesToKeep</c>.</summary>
    public const int DefaultProviderBudgetRecentMessagesToKeep = 6;

    /// <summary>The <c>ProviderCallBudgeter</c> floor: the last exchange always survives a trim.</summary>
    public const int MinProviderBudgetRecentMessagesToKeep = 2;

    public const int MaxProviderBudgetRecentMessagesToKeep = 100;

    /// <summary>Hardcoded fallback; mirrors <c>ProviderCallBudgetOptions.MaxCumulativeInputTokens</c>.</summary>
    public const int DefaultProviderBudgetMaxCumulativeInputTokens = 4_000_000;

    public const int MinProviderBudgetMaxCumulativeInputTokens = 100_000;

    public const int MaxProviderBudgetMaxCumulativeInputTokens = 100_000_000;

    /// <summary>Hardcoded fallback; mirrors <c>ConversationContextBudgetOptions.RecentTurnKeepCount</c>.</summary>
    public const int DefaultContextBudgetRecentTurnKeepCount = 4;

    /// <summary>The budgeter floor: an approval replay splits one round across two turns.</summary>
    public const int MinContextBudgetRecentTurnKeepCount = 2;

    public const int MaxContextBudgetRecentTurnKeepCount = 50;

    /// <summary>Mirrors <c>ConversationCompactionOptions.AutoCompactEnabled</c>.</summary>
    public const bool DefaultCompactionAutoEnabled = true;

    /// <summary>Hardcoded fallback, in percent; mirrors <c>ConversationCompactionOptions.AutoCompactFraction</c> (0.75).</summary>
    public const int DefaultCompactionAutoCompactPercent = 75;

    /// <summary>The <c>ConversationCompactionOptions.AutoCompactFraction</c> range 0.3..0.95, in percent.</summary>
    public const int MinCompactionAutoCompactPercent = 30;

    public const int MaxCompactionAutoCompactPercent = 95;

    /// <summary>Hardcoded fallback; mirrors <c>ConversationCompactionOptions.RecentMessagesToKeepVerbatim</c>.</summary>
    public const int DefaultCompactionRecentMessagesVerbatim = 8;

    public const int MinCompactionRecentMessagesVerbatim = 2;

    public const int MaxCompactionRecentMessagesVerbatim = 100;

    /// <summary>Mirrors <c>ConversationCompactionOptions.DistillEnabled</c>.</summary>
    public const bool DefaultCompactionDistillEnabled = true;

    /// <summary>Hardcoded fallback; mirrors <c>LocalChatAgentOptions.MaxInlinedAttachmentChars</c>.</summary>
    public const int DefaultMaxInlinedAttachmentChars = 48_000;

    public const int MinMaxInlinedAttachmentChars = 1000;

    public const int MaxMaxInlinedAttachmentChars = 2_000_000;

    /// <summary>Hardcoded fallback; mirrors <c>LocalChatAgentOptions.KnowledgeChatTopK</c>.</summary>
    public const int DefaultKnowledgeChatTopK = 5;

    public const int MinKnowledgeChatTopK = 1;

    public const int MaxKnowledgeChatTopK = 20;

    /// <summary>Mirrors <c>ProviderResilienceOptions.RetryEnabled</c>.</summary>
    public const bool DefaultProviderRetryEnabled = true;

    /// <summary>Hardcoded fallback; mirrors <c>ProviderResilienceOptions.MaxRetries</c>.</summary>
    public const int DefaultProviderMaxRetries = 2;

    public const int MinProviderMaxRetries = 0;

    public const int MaxProviderMaxRetries = 10;

    /// <summary>Hardcoded fallback; mirrors <c>SpawnOptions.MaxConcurrentSpawns</c>.</summary>
    public const int DefaultSpawnMaxConcurrent = 3;

    /// <summary><c>SpawnContext.BeginRoot</c> rejects a fan-out cap below one.</summary>
    public const int MinSpawnMaxConcurrent = 1;

    public const int MaxSpawnMaxConcurrent = 32;

    /// <summary>Hardcoded fallback; mirrors <c>SpawnOptions.MaxCloudSpawns</c>.</summary>
    public const int DefaultSpawnMaxCloud = 3;

    /// <summary><c>0</c> forbids cloud sub-agents for the run.</summary>
    public const int MinSpawnMaxCloud = 0;

    public const int MaxSpawnMaxCloud = 32;

    /// <summary>Hardcoded fallback; mirrors <c>SpawnOptions.QueueWaitSeconds</c>.</summary>
    public const int DefaultSpawnQueueWaitSeconds = 120;

    /// <summary><c>0</c> rejects a same-model sub-agent at once instead of queueing it.</summary>
    public const int MinSpawnQueueWaitSeconds = 0;

    public const int MaxSpawnQueueWaitSeconds = 3600;

    // Knowledge, privacy and usage knobs (round 2). Each Default mirrors its options class default, which the shipped appsettings.json
    // does not override.

    /// <summary>Mirrors <c>KnowledgeBaseOptions.AdaptiveRerankingEnabled</c>.</summary>
    public const bool DefaultKnowledgeAdaptiveRerankingEnabled = true;

    /// <summary>Hardcoded fallback; mirrors <c>KnowledgeBaseOptions.RetrievalLatencyBudgetMilliseconds</c>.</summary>
    public const int DefaultKnowledgeRetrievalLatencyBudgetMs = 500;

    public const int MinKnowledgeRetrievalLatencyBudgetMs = 50;

    public const int MaxKnowledgeRetrievalLatencyBudgetMs = 60_000;

    /// <summary>Mirrors <c>KnowledgeBaseOptions.ScheduledModelReindexEnabled</c>.</summary>
    public const bool DefaultKnowledgeScheduledReindexEnabled = true;

    /// <summary>Hardcoded fallback; mirrors <c>KnowledgeBaseOptions.ScheduledModelReindexIntervalMinutes</c>.</summary>
    public const int DefaultKnowledgeScheduledReindexIntervalMinutes = 60;

    public const int MinKnowledgeScheduledReindexIntervalMinutes = 5;

    /// <summary>One week.</summary>
    public const int MaxKnowledgeScheduledReindexIntervalMinutes = 10_080;

    /// <summary>Mirrors <c>KnowledgeBaseOptions.AgentToolsEnabled</c>.</summary>
    public const bool DefaultKnowledgeAgentToolsEnabled = true;

    /// <summary>Mirrors <c>KnowledgeBaseOptions.AllowCloudModelAccess</c>: node-local data never reaches a cloud model unless opted in.</summary>
    public const bool DefaultAllowCloudModelAccess = false;

    /// <summary>Mirrors <c>ChatRetentionOptions.Enabled</c>: retention deletes chat history, so it is off unless opted in.</summary>
    public const bool DefaultChatRetentionEnabled = false;

    /// <summary>Hardcoded fallback; mirrors <c>ChatRetentionOptions.RetentionDays</c>.</summary>
    public const int DefaultChatRetentionDays = 30;

    /// <summary>Hardcoded fallback; mirrors <c>AgentExecutionLogRetentionOptions.Enabled</c>.</summary>
    public const bool DefaultAgentExecutionLogRetentionEnabled = true;

    /// <summary>Hardcoded fallback; mirrors <c>AgentExecutionLogRetentionOptions.RetentionDays</c>.</summary>
    public const int DefaultAgentExecutionLogRetentionDays = 30;

    /// <summary>Hardcoded fallback; mirrors <c>SchedulerOptions.HistoryRetentionDays</c>.</summary>
    public const int DefaultSchedulerHistoryRetentionDays = 30;

    /// <summary>
    ///     Shared bounds of the three day-count retention windows. The floor of one day keeps the <c>now - days</c> cutoff in the past,
    ///     so a window can never purge everything at once.
    /// </summary>
    public const int MinRetentionDays = 1;

    /// <summary>Ten years.</summary>
    public const int MaxRetentionDays = 3650;

    /// <summary>Hardcoded fallback; mirrors <c>NodeDbBackupOptions.RetainCount</c>.</summary>
    public const int DefaultNodeDbBackupRetainCount = 3;

    /// <summary>The <c>NodeDbBackupOptions</c> validator floor: the newest snapshot always survives a prune.</summary>
    public const int MinNodeDbBackupRetainCount = 1;

    public const int MaxNodeDbBackupRetainCount = 100;

    /// <summary>
    ///     Hardcoded fallback, 64 GiB, for the base-logit cache that evicts whole files least recently used first. A single base model
    ///     at 200 chunks is ~25 GB, so this is "two of them and a little room", not a generous allowance.
    /// </summary>
    public const long DefaultBenchmarkKldCacheMaxBytes = 64L * 1024 * 1024 * 1024;

    /// <summary>1 GiB.</summary>
    public const long MinBenchmarkKldCacheMaxBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>4 TiB.</summary>
    public const long MaxBenchmarkKldCacheMaxBytes = 4L * 1024 * 1024 * 1024 * 1024;

    // Runtime and workspace knobs (round 2). Each Default mirrors its options class default, which the shipped appsettings.json does
    // not override; each Min/Max pair sits inside that options class's own validator bounds.

    /// <summary>Mirrors <c>StableDiffusionRuntimeOptions.MaxLoadedProcesses</c>.</summary>
    public const int DefaultImageMaxLoadedProcesses = 1;

    /// <summary>At least one, or every image load would evict itself.</summary>
    public const int MinImageMaxLoadedProcesses = 1;

    public const int MaxImageMaxLoadedProcesses = 4;

    /// <summary>Mirrors <c>StableDiffusionRuntimeOptions.TextEncoderOnGpu</c>.</summary>
    public const bool DefaultImageTextEncoderOnGpu = false;

    /// <summary>Mirrors <c>GraphWorkflowOptions.MaxConcurrentRuns</c>.</summary>
    public const int DefaultGraphWorkflowMaxConcurrentRuns = 4;

    public const int MinGraphWorkflowMaxConcurrentRuns = 1;

    public const int MaxGraphWorkflowMaxConcurrentRuns = 64;

    /// <summary>Mirrors <c>GraphWorkflowOptions.DefaultNodeTimeoutSeconds</c>.</summary>
    public const int DefaultGraphWorkflowDefaultNodeTimeoutSeconds = 600;

    public const int MinGraphWorkflowDefaultNodeTimeoutSeconds = 30;

    /// <summary>One day.</summary>
    public const int MaxGraphWorkflowDefaultNodeTimeoutSeconds = 86_400;

    /// <summary>Mirrors <c>WorkSessionOptions.MaxStepsPerRun</c>.</summary>
    public const int DefaultWorkSessionMaxStepsPerRun = 25;

    public const int MinWorkSessionMaxStepsPerRun = 1;

    public const int MaxWorkSessionMaxStepsPerRun = 1000;

    /// <summary>Mirrors <c>WorkSessionOptions.MaxConcurrentSessions</c>.</summary>
    public const int DefaultWorkSessionMaxConcurrentSessions = 1;

    public const int MinWorkSessionMaxConcurrentSessions = 1;

    public const int MaxWorkSessionMaxConcurrentSessions = 64;

    /// <summary>Mirrors <c>DevelopmentOptions.MaxAttemptDurationSeconds</c> (30 minutes).</summary>
    public const int DefaultDevelopmentMaxAttemptDurationSeconds = 1800;

    public const int MinDevelopmentMaxAttemptDurationSeconds = 60;

    /// <summary>One day.</summary>
    public const int MaxDevelopmentMaxAttemptDurationSeconds = 86_400;

    /// <summary>Mirrors <c>DevelopmentOptions.MaxToolCalls</c>.</summary>
    public const int DefaultDevelopmentMaxToolCalls = 64;

    public const int MinDevelopmentMaxToolCalls = 1;

    public const int MaxDevelopmentMaxToolCalls = 1024;

    /// <summary>Mirrors <c>DevelopmentOptions.MaxOutputTokens</c>.</summary>
    public const int DefaultDevelopmentMaxOutputTokens = 32_768;

    public const int MinDevelopmentMaxOutputTokens = 256;

    public const int MaxDevelopmentMaxOutputTokens = 1_000_000;

    /// <summary>Mirrors <c>AgentHomeOptions.MaxInnerToolCalls</c>.</summary>
    public const int DefaultAgentHomeMaxInnerToolCalls = 24;

    public const int MinAgentHomeMaxInnerToolCalls = 1;

    public const int MaxAgentHomeMaxInnerToolCalls = 1000;

    /// <summary>Mirrors <c>AgentHomeOptions.PatchApplyTimeoutSeconds</c> and the shipped appsettings.json.</summary>
    public const int DefaultAgentHomePatchApplyTimeoutSeconds = 120;

    public const int MinAgentHomePatchApplyTimeoutSeconds = 10;

    /// <summary>One hour.</summary>
    public const int MaxAgentHomePatchApplyTimeoutSeconds = 3600;

    /// <summary>Mirrors <c>AgentHomeRunRetentionOptions.MaxRuns</c>.</summary>
    public const int DefaultAgentHomeRunRetentionMaxRuns = 200;

    /// <summary>0 turns the run-count limit off.</summary>
    public const int MinAgentHomeRunRetentionMaxRuns = 0;

    public const int MaxAgentHomeRunRetentionMaxRuns = 100_000;

    /// <summary>Mirrors <c>AgentHomeRunRetentionOptions.MaxTotalBytes</c> (2 GiB).</summary>
    public const long DefaultAgentHomeRunRetentionMaxTotalBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>0 turns the byte limit off.</summary>
    public const long MinAgentHomeRunRetentionMaxTotalBytes = 0;

    /// <summary>1 TiB, the retention validator's ceiling.</summary>
    public const long MaxAgentHomeRunRetentionMaxTotalBytes = 1024L * 1024 * 1024 * 1024;

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
    ///     Node switch for the built-in web tools (<c>web_fetch</c>, <c>web_search</c>); default (absent) is OFF. When
    ///     off they are offered to no model and the web-access service refuses every call. ADR 0017.
    /// </summary>
    public const bool DefaultWebAccessEnabled = false;

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
    ///     Node switch for the built-in web tools. <see langword="null" /> (absent) reads as
    ///     <see cref="DefaultWebAccessEnabled" /> (off). Read per offer and per call, so a save applies to the next turn.
    /// </summary>
    public bool? WebAccessEnabled { get; init; }

    /// <summary>
    ///     Operator-configured SearXNG base URL that replaces the DuckDuckGo backend of <c>web_search</c>;
    ///     <see langword="null" /> uses DuckDuckGo. <c>NodeSettingsStore.Normalize</c> keeps only an absolute http(s) URL,
    ///     and a blank save clears it, like <see cref="OllamaEndpoint" />.
    /// </summary>
    public string? WebSearchSearxngUrl { get; init; }

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
    /// </summary>
    public int? TranscriptionIdleTimeoutMinutes { get; init; }

    /// <summary>
    ///     The llama-server readiness deadline cap in seconds. Seed: 600. Applies on the next node restart (seeded into
    ///     the supervisor options at host build).
    /// </summary>
    public int? LlamaReadinessTimeoutCapSeconds { get; init; }

    /// <summary>Chat-role HTTP timeout to a llama-server in seconds. Seed: 3600. Applies on the next node restart.</summary>
    public int? LlamaChatHttpTimeoutSeconds { get; init; }

    /// <summary>Embedding/rerank-role HTTP timeout to a llama-server in seconds. Seed: 600. Applies on the next node restart.</summary>
    public int? LlamaEmbeddingHttpTimeoutSeconds { get; init; }

    /// <summary>
    ///     Chat-role host prompt-cache budget in MiB (<c>--cache-ram</c>); <c>0</c> disables it. <see langword="null" />
    ///     (absent) is automatic: one eighth of RAM, clamped to 512–8192 MiB. Applies on the next node restart.
    /// </summary>
    public int? LlamaChatCacheRamMiB { get; init; }

    /// <summary>CPU threads left free for the host when a spawn derives its thread count. Seed: 1. Applies on the next node restart.</summary>
    public int? LlamaCpuThreadReserve { get; init; }

    /// <summary>
    ///     Share of GPU memory, in percent, the context allocator keeps free. Seed: 5. Applies on the next node restart,
    ///     and CHANGING IT invalidates every frozen inference profile (it is part of the launch-policy fingerprint).
    /// </summary>
    public int? LlamaGpuReservePercent { get; init; }

    /// <summary>Share of RAM, in percent, the context allocator keeps free. Seed: 15. Same restart and fingerprint rules as <see cref="LlamaGpuReservePercent" />.</summary>
    public int? LlamaRamReservePercent { get; init; }

    /// <summary>Idle TTL (seconds) of an sd-server daemon. Seed: <c>StableDiffusionRuntime:IdleTimeToLive</c> (900). Applies on the next node restart.</summary>
    public int? ImageIdleTimeToLiveSeconds { get; init; }

    /// <summary>Model-fit safety margin in percent of weights + KV. Seed: 12. Read per estimate, so a save applies to the next fit.</summary>
    public int? ModelFitSafetyMarginPercent { get; init; }

    /// <summary>
    ///     Ceiling on raw provider rounds per invocation. Seed: <c>Agent:ProviderCallBudget:MaxProviderCallsPerInvocation</c>
    ///     (200). Applies on the next node restart.
    /// </summary>
    public int? MaxProviderCallsPerInvocation { get; init; }

    /// <summary>Ceiling on a custom command tool's timeout in seconds. Seed: 300. Read per save and per call.</summary>
    public int? CustomToolMaxTimeoutSeconds { get; init; }

    /// <summary>Time budget of one <c>web_fetch</c> or <c>web_search</c> call in seconds. Seed: 20. Read per call.</summary>
    public int? WebFetchTimeoutSeconds { get; init; }

    /// <summary>Cap on the readable text one <c>web_fetch</c> returns, in characters. Seed: 12000. Read per call.</summary>
    public int? WebFetchMaxContentChars { get; init; }

    /// <summary>
    ///     <c>search_knowledge_base</c> hit count when the model names none. Seed: 5; must not exceed
    ///     <see cref="KnowledgeSearchMaxResults" />. Read per call.
    /// </summary>
    public int? KnowledgeSearchDefaultResults { get; init; }

    /// <summary>Ceiling on the <c>search_knowledge_base</c> hit count. Seed: 20. Read per call.</summary>
    public int? KnowledgeSearchMaxResults { get; init; }

    /// <summary>
    ///     Parallel range connections per large model download. Seed: <c>HuggingFace:DownloadConnections</c> (4).
    ///     Applies on the next node restart.
    /// </summary>
    public int? HuggingFaceDownloadConnections { get; init; }

    /// <summary>Whisper.cpp per-request inference timeout in minutes. Seed: 30. Applies on the next node restart.</summary>
    public int? TranscriptionInferenceTimeoutMinutes { get; init; }

    /// <summary>
    ///     AgentHome whole-run wall clock in seconds (developer-only). Seed: <c>AgentHome:MaxRunSeconds</c> (600); must be
    ///     at least the effective command timeout. Read per run.
    /// </summary>
    public int? AgentHomeMaxRunSeconds { get; init; }

    /// <summary>AgentHome run-folder retention in days. Seed: <c>AgentHome:RunRetention:RetentionDays</c> (30). Read per sweep.</summary>
    public int? AgentHomeRunRetentionDays { get; init; }

    /// <summary>
    ///     Tool-loop iterations per request. Seed: <c>Agent:ToolPipeline:MaximumToolIterationsPerRequest</c> (40). Applies on
    ///     the next node restart (the chat-client pipeline is built once).
    /// </summary>
    public int? ToolPipelineMaxIterationsPerRequest { get; init; }

    /// <summary>
    ///     Characters one tool result may hand the model. Seed: <c>Agent:ToolPipeline:MaxToolResultCharacters</c> (65536).
    ///     Applies on the next node restart.
    /// </summary>
    public int? ToolPipelineMaxToolResultChars { get; init; }

    /// <summary>
    ///     Invalid calls to one tool in a row before the loop gives up on it. Seed:
    ///     <c>Agent:ToolPipeline:MaxConsecutiveInvalidToolCallsPerTool</c> (3). Applies on the next node restart.
    /// </summary>
    public int? ToolPipelineMaxConsecutiveInvalidToolCalls { get; init; }

    /// <summary>
    ///     Context window assumed when a send names none, for both the turn budget and the provider-round budget. Seed:
    ///     <c>Agent:ProviderCallBudget:DefaultContextTokens</c> (8192). Read per turn.
    /// </summary>
    public int? DefaultContextTokens { get; init; }

    /// <summary>Recent messages a provider-round trim always keeps. Seed: <c>Agent:ProviderCallBudget:RecentMessagesToKeep</c> (6). Read per turn.</summary>
    public int? ProviderBudgetRecentMessagesToKeep { get; init; }

    /// <summary>Input tokens one agent run may send in total. Seed: <c>Agent:ProviderCallBudget:MaxCumulativeInputTokens</c> (4000000). Read per turn.</summary>
    public int? ProviderBudgetMaxCumulativeInputTokens { get; init; }

    /// <summary>Recent turns the history budget never trims. Seed: <c>Agent:ConversationContextBudget:RecentTurnKeepCount</c> (4). Read per budget pass.</summary>
    public int? ContextBudgetRecentTurnKeepCount { get; init; }

    /// <summary>Whether a long chat is compacted automatically after a turn. Seed: <c>Agent:ConversationCompaction:AutoCompactEnabled</c> (on). Read per job.</summary>
    public bool? CompactionAutoEnabled { get; init; }

    /// <summary>
    ///     Share of the usable window, in percent, above which a chat is compacted. Seed:
    ///     <c>Agent:ConversationCompaction:AutoCompactFraction</c> (0.75, stored as 75). Read per job.
    /// </summary>
    public int? CompactionAutoCompactPercent { get; init; }

    /// <summary>Recent messages a compaction keeps word for word. Seed: <c>Agent:ConversationCompaction:RecentMessagesToKeepVerbatim</c> (8). Read per compaction.</summary>
    public int? CompactionRecentMessagesVerbatim { get; init; }

    /// <summary>Whether conversation state is distilled in the background. Seed: <c>Agent:ConversationCompaction:DistillEnabled</c> (on). Read per job.</summary>
    public bool? CompactionDistillEnabled { get; init; }

    /// <summary>Attachment text inlined into one chat turn, in characters. Seed: <c>Agent:LocalChat:MaxInlinedAttachmentChars</c> (48000). Read per turn.</summary>
    public int? MaxInlinedAttachmentChars { get; init; }

    /// <summary>Knowledge-base passages grounded into one chat turn. Seed: <c>Agent:LocalChat:KnowledgeChatTopK</c> (5). Read per turn.</summary>
    public int? KnowledgeChatTopK { get; init; }

    /// <summary>Whether a failed model send is retried before its first token. Seed: <c>Agent:ProviderResilience:RetryEnabled</c> (on). Read per send.</summary>
    public bool? ProviderRetryEnabled { get; init; }

    /// <summary>Retries of a failed model send. Seed: <c>Agent:ProviderResilience:MaxRetries</c> (2). Read per send.</summary>
    public int? ProviderMaxRetries { get; init; }

    /// <summary>Sub-agents one run may have in flight at once. Seed: <c>Spawn:MaxConcurrentSpawns</c> (3). Read per root run.</summary>
    public int? SpawnMaxConcurrent { get; init; }

    /// <summary>Cloud sub-agents one run may start. Seed: <c>Spawn:MaxCloudSpawns</c> (3). Read per root run.</summary>
    public int? SpawnMaxCloud { get; init; }

    /// <summary>How long a sub-agent waits for its busy model, in seconds. Seed: <c>Spawn:QueueWaitSeconds</c> (120). Read per spawn.</summary>
    public int? SpawnQueueWaitSeconds { get; init; }

    /// <summary>
    ///     Whether a configured reranker runs only for ambiguous candidate sets. Seed: <c>KnowledgeBase:AdaptiveRerankingEnabled</c> (on).
    ///     Read per search.
    /// </summary>
    public bool? KnowledgeAdaptiveRerankingEnabled { get; init; }

    /// <summary>
    ///     Retrieval latency budget in milliseconds. Seed: <c>KnowledgeBase:RetrievalLatencyBudgetMilliseconds</c> (500). Read per search.
    /// </summary>
    public int? KnowledgeRetrievalLatencyBudgetMs { get; init; }

    /// <summary>
    ///     Whether stale-vector documents are re-queued after an embedding-model change. Seed:
    ///     <c>KnowledgeBase:ScheduledModelReindexEnabled</c> (on). Applies on the next node restart.
    /// </summary>
    public bool? KnowledgeScheduledReindexEnabled { get; init; }

    /// <summary>
    ///     Stale-vector polling interval in minutes. Seed: <c>KnowledgeBase:ScheduledModelReindexIntervalMinutes</c> (60). Applies on
    ///     the next node restart.
    /// </summary>
    public int? KnowledgeScheduledReindexIntervalMinutes { get; init; }

    /// <summary>
    ///     Whether the knowledge-base agent tools are offered and run. Seed: <c>KnowledgeBase:AgentToolsEnabled</c> (on). Read per offer,
    ///     turn and tool call.
    /// </summary>
    public bool? KnowledgeAgentToolsEnabled { get; init; }

    /// <summary>
    ///     Whether a cloud-hosted model may receive node-local data (knowledge tools, workspace files, attachments, playbook memory).
    ///     Seed: <c>KnowledgeBase:AllowCloudModelAccess</c> (off). Read per turn. Not part of the external-access preset.
    /// </summary>
    public bool? AllowCloudModelAccess { get; init; }

    /// <summary>
    ///     Model that proposes playbook actions; blank inherits. Seed: <c>PlaybookAnalysis:ModelName</c>, then the default model. Read
    ///     per run.
    /// </summary>
    public string? PlaybookAnalysisModelName { get; init; }

    /// <summary>Model that runs playbook evals; blank inherits. Seed: <c>PlaybookEval:ModelName</c>, then the default model. Read per run.</summary>
    public string? PlaybookEvalModelName { get; init; }

    /// <summary>
    ///     Model that mines lessons from completed runs; blank inherits. Seed: <c>MemoryExtraction:ExtractionModelName</c>, then the
    ///     default model. Read per run.
    /// </summary>
    public string? MemoryExtractionModelName { get; init; }

    /// <summary>Whether old conversations are deleted. Seed: <c>ChatRetention:Enabled</c> (off). Read per sweep.</summary>
    public bool? ChatRetentionEnabled { get; init; }

    /// <summary>Conversation retention window in days. Seed: <c>ChatRetention:RetentionDays</c> (30). Read per sweep.</summary>
    public int? ChatRetentionDays { get; init; }

    /// <summary>Whether old agent execution logs are deleted. Seed: <c>AgentExecutionLogRetention:Enabled</c> (on). Read per sweep.</summary>
    public bool? AgentExecutionLogRetentionEnabled { get; init; }

    /// <summary>
    ///     Agent execution-log retention window in days. Seed: <c>AgentExecutionLogRetention:RetentionDays</c> (30). Read per sweep and
    ///     per usage-summary request.
    /// </summary>
    public int? AgentExecutionLogRetentionDays { get; init; }

    /// <summary>Node database snapshots kept. Seed: <c>NodeDbBackup:RetainCount</c> (3). Read per backup.</summary>
    public int? NodeDbBackupRetainCount { get; init; }

    /// <summary>Benchmark KL-divergence base cache ceiling in bytes. Seed: <c>Benchmarks:KldCacheMaxBytes</c> (64 GiB). Read per trim.</summary>
    public long? BenchmarkKldCacheMaxBytes { get; init; }

    /// <summary>Scheduled-job run history window in days. Seed: <c>Scheduler:HistoryRetentionDays</c> (30). Read per sweep.</summary>
    public int? SchedulerHistoryRetentionDays { get; init; }

    /// <summary>sd-server processes kept loaded at once. Seed: <c>StableDiffusionRuntime:MaxLoadedProcesses</c> (1). Applies on the next node restart.</summary>
    public int? ImageMaxLoadedProcesses { get; init; }

    /// <summary>Whether sd-server keeps the text encoder on the GPU. Seed: <c>StableDiffusionRuntime:TextEncoderOnGpu</c> (off). Applies on the next node restart.</summary>
    public bool? ImageTextEncoderOnGpu { get; init; }

    /// <summary>Graph-workflow runs live at once. Seed: <c>GraphWorkflows:MaxConcurrentRuns</c> (4). Applies on the next node restart.</summary>
    public int? GraphWorkflowMaxConcurrentRuns { get; init; }

    /// <summary>
    ///     Graph-workflow node timeout in seconds when a node names none. Seed: <c>GraphWorkflows:DefaultNodeTimeoutSeconds</c> (600).
    ///     Applies on the next node restart.
    /// </summary>
    public int? GraphWorkflowDefaultNodeTimeoutSeconds { get; init; }

    /// <summary>Work-session steps per start or resume. Seed: <c>WorkSessions:MaxStepsPerRun</c> (25). Applies on the next node restart.</summary>
    public int? WorkSessionMaxStepsPerRun { get; init; }

    /// <summary>Work sessions running at once. Seed: <c>WorkSessions:MaxConcurrentSessions</c> (1). Applies on the next node restart.</summary>
    public int? WorkSessionMaxConcurrentSessions { get; init; }

    /// <summary>
    ///     Development attempt wall clock in seconds. Seed: <c>Development:MaxAttemptDurationSeconds</c> (1800). Applies on the next node
    ///     restart.
    /// </summary>
    public int? DevelopmentMaxAttemptDurationSeconds { get; init; }

    /// <summary>Tool calls per development attempt. Seed: <c>Development:MaxToolCalls</c> (64). Applies on the next node restart.</summary>
    public int? DevelopmentMaxToolCalls { get; init; }

    /// <summary>Output tokens per development attempt. Seed: <c>Development:MaxOutputTokens</c> (32768). Applies on the next node restart.</summary>
    public int? DevelopmentMaxOutputTokens { get; init; }

    /// <summary>AgentHome inner tool calls per run (developer-only). Seed: <c>AgentHome:MaxInnerToolCalls</c> (24). Read per run.</summary>
    public int? AgentHomeMaxInnerToolCalls { get; init; }

    /// <summary>AgentHome patch-apply git timeout in seconds (developer-only). Seed: <c>AgentHome:PatchApplyTimeoutSeconds</c> (120). Read per apply.</summary>
    public int? AgentHomePatchApplyTimeoutSeconds { get; init; }

    /// <summary>AgentHome runs kept, 0 = no count limit. Seed: <c>AgentHome:RunRetention:MaxRuns</c> (200). Read per sweep.</summary>
    public int? AgentHomeRunRetentionMaxRuns { get; init; }

    /// <summary>AgentHome runs byte ceiling, 0 = no byte limit. Seed: <c>AgentHome:RunRetention:MaxTotalBytes</c> (2 GiB). Read per sweep.</summary>
    public long? AgentHomeRunRetentionMaxTotalBytes { get; init; }

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
    ///     Stable, LOCAL-ONLY machine identifier used to key inference profiles to the host they were tuned on.
    /// </summary>
    /// <remarks>
    ///     Generated once (<see cref="System.Guid.NewGuid" />, <c>"N"</c> format) by <c>IMachineKeyProvider</c> on first
    ///     use and persisted here; <see langword="null" /> until then (it is generated, not seeded — there is no
    ///     appsettings default). NEVER emitted in telemetry, aggregates, or logs.
    /// </remarks>
    public string? MachineKey { get; init; }
}
