namespace XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;

using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Response for <c>GET api/local/v1/node-settings</c>: the effective stored value of every user-editable node
///     setting, plus the per-field bounds the React form renders ranges from.
/// </summary>
/// <remarks>
///     Fields are grouped: the original chat timeout, then the "general" migrated knobs (always shown), then the
///     developer-only advanced knobs. The React lane gates the advanced knobs' visibility; the backend always returns
///     them.
/// </remarks>
public sealed record NodeSettingsResponse
{
    public int MaxMessageRequestTimeoutSeconds { get; init; }

    public int MinMessageRequestTimeoutSeconds { get; init; }

    public int MaxAllowedMessageRequestTimeoutSeconds { get; init; }

    public string? DefaultModelName { get; init; }

    public bool? EnableTools { get; init; }

    /// <summary>
    ///     Node kill-switch for the user-defined custom tools feature. <see langword="null" /> reads as off (default).
    /// </summary>
    /// <remarks>
    ///     DANGER: enabling this allows agents to run user-defined tools that execute host commands, launch programs
    ///     and make network requests. Each call still requires operator approval, per the per-agent allow-list and the
    ///     forced per-call approval gate.
    /// </remarks>
    public bool? CustomToolsEnabled { get; init; }

    /// <summary>
    ///     Node switch for the per-turn tool-relevance offer. <see langword="null" /> reads as off (default).
    /// </summary>
    /// <remarks>
    ///     When on, an agent carrying many tools is offered a relevance-ranked subset per turn and recovers the rest
    ///     through <c>list_tools</c>; a per-agent opt-out still wins. A context budget, never an authorisation
    ///     boundary.
    /// </remarks>
    public bool? ToolRelevanceEnabled { get; init; }

    /// <summary>
    ///     Node switch for the built-in web tools (<c>web_fetch</c>, <c>web_search</c>). <see langword="null" /> reads
    ///     as off (default).
    /// </summary>
    /// <remarks>
    ///     When on, a tool-capable node-local model may send search queries to DuckDuckGo (or the configured SearXNG)
    ///     and download public web pages. What they retrieve is shown to the user for review before it reaches the
    ///     model, unless the conversation's auto-accept is on.
    /// </remarks>
    public bool? WebAccessEnabled { get; init; }

    /// <summary>
    ///     The SearXNG base URL that replaces DuckDuckGo for <c>web_search</c>; <see langword="null" /> uses DuckDuckGo.
    /// </summary>
    public string? WebSearchSearxngUrl { get; init; }

    /// <summary>
    ///     Which external-access preset was last applied: <c>recommended</c>, <c>offline</c>, <c>custom</c> (the
    ///     switches no longer match a preset), <c>pending</c> (an administrator exists and nobody has chosen yet), or
    ///     <see langword="null" /> (undecided, with no administrator).
    /// </summary>
    /// <remarks>
    ///     A RECORD of the choice, never the authority — the three switches below are what every gate reads — and
    ///     <c>custom</c> and <c>pending</c> are engine-written, the server being their only writer. Honesty rule both
    ///     surfaces must render: Offline disables exactly the three checks named below and NOTHING else. It does not
    ///     block the connection to the C0re platform, MCP servers the operator has configured, or model-catalog
    ///     lookups. Copy that implies a network kill switch is a privacy misrepresentation.
    /// </remarks>
    public string? ExternalAccessProfile { get; init; }

    /// <summary>
    ///     Which navigation mode this node shows: <c>simple</c> (the everyday surfaces only) or <c>advanced</c>
    ///     (everything this build offers).
    /// </summary>
    /// <remarks>
    ///     <see langword="null" /> means the first-run question has not been answered yet — the SPA's onboarding step
    ///     keys on exactly that, and reads null as <c>advanced</c> everywhere else. Presentation only: it hides
    ///     navigation entries and nothing more, every route stays reachable by URL, no server-side gate consults it,
    ///     and the build's capability flags still decide what exists.
    /// </remarks>
    public string? UiMode { get; init; }

    /// <summary>
    ///     Which application-update channel this node follows: <c>stable</c>, <c>preview</c> or <c>development</c>.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" /> means the operator has never chosen; the node follows the channel baked into this
    ///     build.
    /// </remarks>
    public string? UpdateChannel { get; init; }

    /// <summary>
    ///     Whether the node checks for application updates on its own. <see langword="null" /> reads as on. The manual
    ///     check and apply flow are unaffected. The check runs once per process, so turning it back on takes effect at
    ///     the next node start.
    /// </summary>
    public bool? AutoCheckApplicationUpdates { get; init; }

    /// <summary>
    ///     Whether the node checks for llama.cpp / runtime updates on its own. <see langword="null" /> reads as on.
    /// </summary>
    /// <remarks>
    ///     The manual runtime-status refresh and the runtime install are unaffected. The check runs once per process,
    ///     so turning it back on takes effect at the next node start.
    /// </remarks>
    public bool? AutoCheckRuntimeUpdates { get; init; }

    /// <summary>
    ///     Whether the node downloads a first-run model and runtime on its own. <see langword="null" /> reads as on. A
    ///     manual model download or install is unaffected. It applies on a first run only, so turning it back on takes
    ///     effect at the next node start.
    /// </summary>
    public bool? AutoProvisionFirstRunModel { get; init; }

    public IReadOnlyList<string>? ToolCapableModels { get; init; }

    public string? OllamaEndpoint { get; init; }

    public string? HuggingFaceDefaultQuant { get; init; }

    public int? LlamaMaxLoadedProcesses { get; init; }

    public int MinLlamaMaxLoadedProcesses { get; init; }

    public int MaxAllowedLlamaMaxLoadedProcesses { get; init; }

    public int? LlamaIdleTimeToLiveSeconds { get; init; }

    public int MinLlamaIdleTimeToLiveSeconds { get; init; }

    public int MaxAllowedLlamaIdleTimeToLiveSeconds { get; init; }

    public bool? KeepModelWarmEnabled { get; init; }

    public string? KeepModelWarmModelName { get; init; }

    public int? KeepModelWarmIntervalSeconds { get; init; }

    public int MinKeepModelWarmIntervalSeconds { get; init; }

    public int MaxAllowedKeepModelWarmIntervalSeconds { get; init; }

    public int? MaxResponseSizeMb { get; init; }

    public int MinMaxResponseSizeMb { get; init; }

    public int MaxAllowedMaxResponseSizeMb { get; init; }

    public string? RecommendedLlamaCppTag { get; init; }

    public int? ChatCacheReuse { get; init; }

    public int MinChatCacheReuse { get; init; }

    public int MaxAllowedChatCacheReuse { get; init; }

    public string? SpeculativeMode { get; init; }

    /// <summary>KV-cache element type for GPU chat spawns: <c>f16</c> | <c>q8_0</c> | <c>q4_0</c>. Null means the node default.</summary>
    public string? KvCacheType { get; init; }

    /// <summary>
    ///     Which container runtime External Apps resolves against: <c>auto</c> picks the one this node can reach,
    ///     <c>docker</c> pins it.
    /// </summary>
    /// <remarks>
    ///     A STRING, not an enum, so a JSON number is rejected by type before any validator runs and the generated
    ///     client models it as a plain string. The resolver never switches provider on its own, so this is the only
    ///     node-wide choice; an instance may still carry its own override.
    /// </remarks>
    public string ContainerRuntimeSelection { get; init; } = StoredNodeSettings.DefaultContainerRuntimeSelection;

    public string? SpeculativeDraftModelName { get; init; }

    public int? SpeculativeDraftMaxTokens { get; init; }

    public int MinSpeculativeDraftMaxTokens { get; init; }

    public int MaxAllowedSpeculativeDraftMaxTokens { get; init; }

    public int? SpeculativeDraftGpuLayers { get; init; }

    public int MinSpeculativeDraftGpuLayers { get; init; }

    public int MaxAllowedSpeculativeDraftGpuLayers { get; init; }

    /// <summary>
    ///     Installed cross-encoder reranker model name for the knowledge-base search rerank stage.
    ///     <see langword="null" />/blank leaves reranking OFF.
    /// </summary>
    public string? RerankerModelName { get; init; }

    public string? AutoEffortFastModelName { get; init; }

    public long? HuggingFaceDiskMarginBytes { get; init; }

    public long MinHuggingFaceDiskMarginBytes { get; init; }

    public long MaxAllowedHuggingFaceDiskMarginBytes { get; init; }

    public int? OrchestrationIdleTimeoutSeconds { get; init; }

    public int MinOrchestrationIdleTimeoutSeconds { get; init; }

    public int MaxAllowedOrchestrationIdleTimeoutSeconds { get; init; }

    public int? AgentHomePrepareTimeoutSeconds { get; init; }

    public int? AgentHomeCommandTimeoutSeconds { get; init; }

    public int MinAgentHomeTimeoutSeconds { get; init; }

    public int MaxAllowedAgentHomeTimeoutSeconds { get; init; }

    public long? AgentHomeMaxSelectedFolderBytes { get; init; }

    public long? AgentHomeMaxPatchBytes { get; init; }

    public int? MaxPendingToolCallAgeMinutes { get; init; }

    public int MinMaxPendingToolCallAgeMinutes { get; init; }

    public int MaxAllowedMaxPendingToolCallAgeMinutes { get; init; }

    /// <summary>Seconds a run with no attached client keeps going before it is cancelled. <c>0</c> never cancels.</summary>
    public int? DetachedGraceSeconds { get; init; }

    public int MinDetachedGraceSeconds { get; init; }

    public int MaxAllowedDetachedGraceSeconds { get; init; }

    /// <summary>Node-level master flag for the client voice feature. <see langword="null" /> reads as off.</summary>
    public bool? VoiceFeatureEnabled { get; init; }

    /// <summary>Preferred browser voice identifier; unmatched legacy values safely fall back to a browser voice.</summary>
    public string? DefaultVoiceProfile { get; init; }

    /// <summary>
    ///     Operator override of usage cost rates, keyed by model NAME to its USD-per-1M input/output rate, flattened
    ///     from the stored <see cref="NodeUsageRateSettings" />.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" /> means no override, so the usage-summary cost estimate falls back to the built-in
    ///     default rate table. Local runtimes are always free regardless.
    /// </remarks>
    public IReadOnlyDictionary<string, ModelRate>? UsageRates { get; init; }

    /// <summary>Whisper.cpp daemon idle time-to-live in minutes. Applies after a node restart.</summary>
    public int? TranscriptionIdleTimeoutMinutes { get; init; }

    public int MinTranscriptionIdleTimeoutMinutes { get; init; }

    public int MaxAllowedTranscriptionIdleTimeoutMinutes { get; init; }

    /// <summary>Cap on how long a llama-server may take to become ready, in seconds. Applies after a node restart.</summary>
    public int? LlamaReadinessTimeoutCapSeconds { get; init; }

    public int MinLlamaReadinessTimeoutCapSeconds { get; init; }

    public int MaxAllowedLlamaReadinessTimeoutCapSeconds { get; init; }

    /// <summary>Chat-role HTTP timeout to a llama-server, in seconds. Applies after a node restart.</summary>
    public int? LlamaChatHttpTimeoutSeconds { get; init; }

    public int MinLlamaChatHttpTimeoutSeconds { get; init; }

    public int MaxAllowedLlamaChatHttpTimeoutSeconds { get; init; }

    /// <summary>Embedding/rerank-role HTTP timeout to a llama-server, in seconds. Applies after a node restart.</summary>
    public int? LlamaEmbeddingHttpTimeoutSeconds { get; init; }

    public int MinLlamaEmbeddingHttpTimeoutSeconds { get; init; }

    public int MaxAllowedLlamaEmbeddingHttpTimeoutSeconds { get; init; }

    /// <summary>
    ///     Chat-role host prompt-cache budget in MiB (<c>--cache-ram</c>); <c>0</c> disables it and <see langword="null" />
    ///     is automatic (one eighth of RAM, 512–8192 MiB). Applies after a node restart.
    /// </summary>
    public int? LlamaChatCacheRamMiB { get; init; }

    public int MinLlamaChatCacheRamMiB { get; init; }

    public int MaxAllowedLlamaChatCacheRamMiB { get; init; }

    /// <summary>CPU threads left free for the host when a spawn derives its thread count. Applies after a node restart.</summary>
    public int? LlamaCpuThreadReserve { get; init; }

    public int MinLlamaCpuThreadReserve { get; init; }

    public int MaxAllowedLlamaCpuThreadReserve { get; init; }

    /// <summary>Share of GPU memory, in percent, the context allocator keeps free. Applies after a node restart and invalidates frozen inference profiles.</summary>
    public int? LlamaGpuReservePercent { get; init; }

    public int MinLlamaGpuReservePercent { get; init; }

    public int MaxAllowedLlamaGpuReservePercent { get; init; }

    /// <summary>Share of RAM, in percent, the context allocator keeps free. Applies after a node restart and invalidates frozen inference profiles.</summary>
    public int? LlamaRamReservePercent { get; init; }

    public int MinLlamaRamReservePercent { get; init; }

    public int MaxAllowedLlamaRamReservePercent { get; init; }

    /// <summary>Idle time-to-live of an sd-server daemon, in seconds. Applies after a node restart.</summary>
    public int? ImageIdleTimeToLiveSeconds { get; init; }

    public int MinImageIdleTimeToLiveSeconds { get; init; }

    public int MaxAllowedImageIdleTimeToLiveSeconds { get; init; }

    /// <summary>Model-fit safety margin in percent of weights plus KV cache. Applies to the next fit estimate.</summary>
    public int? ModelFitSafetyMarginPercent { get; init; }

    public int MinModelFitSafetyMarginPercent { get; init; }

    public int MaxAllowedModelFitSafetyMarginPercent { get; init; }

    /// <summary>Ceiling on raw provider rounds per invocation. Applies after a node restart.</summary>
    public int? MaxProviderCallsPerInvocation { get; init; }

    public int MinMaxProviderCallsPerInvocation { get; init; }

    public int MaxAllowedMaxProviderCallsPerInvocation { get; init; }

    /// <summary>Ceiling on a custom command tool's timeout, in seconds. Applies to the next save or call.</summary>
    public int? CustomToolMaxTimeoutSeconds { get; init; }

    public int MinCustomToolMaxTimeoutSeconds { get; init; }

    public int MaxAllowedCustomToolMaxTimeoutSeconds { get; init; }

    /// <summary>Time budget of one web_fetch or web_search call, in seconds. Applies to the next call.</summary>
    public int? WebFetchTimeoutSeconds { get; init; }

    public int MinWebFetchTimeoutSeconds { get; init; }

    public int MaxAllowedWebFetchTimeoutSeconds { get; init; }

    /// <summary>Cap on the readable text one web_fetch returns, in characters. Applies to the next call.</summary>
    public int? WebFetchMaxContentChars { get; init; }

    public int MinWebFetchMaxContentChars { get; init; }

    public int MaxAllowedWebFetchMaxContentChars { get; init; }

    /// <summary>search_knowledge_base hit count when the model names none; must not exceed the maximum. Applies to the next call.</summary>
    public int? KnowledgeSearchDefaultResults { get; init; }

    public int MinKnowledgeSearchResults { get; init; }

    public int MaxAllowedKnowledgeSearchResults { get; init; }

    /// <summary>Ceiling on the search_knowledge_base hit count. Applies to the next call.</summary>
    public int? KnowledgeSearchMaxResults { get; init; }

    /// <summary>Parallel range connections per large model download. Applies after a node restart.</summary>
    public int? HuggingFaceDownloadConnections { get; init; }

    public int MinHuggingFaceDownloadConnections { get; init; }

    public int MaxAllowedHuggingFaceDownloadConnections { get; init; }

    /// <summary>Whisper.cpp per-request inference timeout, in minutes. Applies after a node restart.</summary>
    public int? TranscriptionInferenceTimeoutMinutes { get; init; }

    public int MinTranscriptionInferenceTimeoutMinutes { get; init; }

    public int MaxAllowedTranscriptionInferenceTimeoutMinutes { get; init; }

    /// <summary>AgentHome whole-run time limit in seconds; must be at least the command timeout. Applies to the next run.</summary>
    public int? AgentHomeMaxRunSeconds { get; init; }

    public int MinAgentHomeMaxRunSeconds { get; init; }

    public int MaxAllowedAgentHomeMaxRunSeconds { get; init; }

    /// <summary>Days an AgentHome run folder is kept. Applies after a node restart. Effective value: stored, else the configuration seed.</summary>
    public int AgentHomeRunRetentionDays { get; init; }

    public int MinAgentHomeRunRetentionDays { get; init; }

    public int MaxAllowedAgentHomeRunRetentionDays { get; init; }

    /// <summary>Tool-loop iterations per request. Applies after a node restart.</summary>
    public int? ToolPipelineMaxIterationsPerRequest { get; init; }

    public int MinToolPipelineMaxIterationsPerRequest { get; init; }

    public int MaxAllowedToolPipelineMaxIterationsPerRequest { get; init; }

    /// <summary>Characters one tool result may hand the model. Applies after a node restart.</summary>
    public int? ToolPipelineMaxToolResultChars { get; init; }

    public int MinToolPipelineMaxToolResultChars { get; init; }

    public int MaxAllowedToolPipelineMaxToolResultChars { get; init; }

    /// <summary>Invalid calls to one tool in a row before it is refused. Applies after a node restart.</summary>
    public int? ToolPipelineMaxConsecutiveInvalidToolCalls { get; init; }

    public int MinToolPipelineMaxConsecutiveInvalidToolCalls { get; init; }

    public int MaxAllowedToolPipelineMaxConsecutiveInvalidToolCalls { get; init; }

    /// <summary>Context window, in tokens, assumed when a send names none. Applies to the next turn.</summary>
    public int? DefaultContextTokens { get; init; }

    public int MinDefaultContextTokens { get; init; }

    public int MaxAllowedDefaultContextTokens { get; init; }

    /// <summary>Recent messages a provider-round trim always keeps. Applies to the next turn.</summary>
    public int? ProviderBudgetRecentMessagesToKeep { get; init; }

    public int MinProviderBudgetRecentMessagesToKeep { get; init; }

    public int MaxAllowedProviderBudgetRecentMessagesToKeep { get; init; }

    /// <summary>Input tokens one agent run may send in total. Applies to the next turn.</summary>
    public int? ProviderBudgetMaxCumulativeInputTokens { get; init; }

    public int MinProviderBudgetMaxCumulativeInputTokens { get; init; }

    public int MaxAllowedProviderBudgetMaxCumulativeInputTokens { get; init; }

    /// <summary>Recent turns the history budget never trims. Applies to the next turn.</summary>
    public int? ContextBudgetRecentTurnKeepCount { get; init; }

    public int MinContextBudgetRecentTurnKeepCount { get; init; }

    public int MaxAllowedContextBudgetRecentTurnKeepCount { get; init; }

    /// <summary>Share of the usable window, in percent, above which a chat is compacted. Applies to the next compaction check.</summary>
    public int? CompactionAutoCompactPercent { get; init; }

    public int MinCompactionAutoCompactPercent { get; init; }

    public int MaxAllowedCompactionAutoCompactPercent { get; init; }

    /// <summary>Recent messages a compaction keeps word for word. Applies to the next compaction.</summary>
    public int? CompactionRecentMessagesVerbatim { get; init; }

    public int MinCompactionRecentMessagesVerbatim { get; init; }

    public int MaxAllowedCompactionRecentMessagesVerbatim { get; init; }

    /// <summary>Attachment text inlined into one chat turn, in characters. Applies to the next turn.</summary>
    public int? MaxInlinedAttachmentChars { get; init; }

    public int MinMaxInlinedAttachmentChars { get; init; }

    public int MaxAllowedMaxInlinedAttachmentChars { get; init; }

    /// <summary>Knowledge-base passages grounded into one chat turn. Applies to the next turn.</summary>
    public int? KnowledgeChatTopK { get; init; }

    public int MinKnowledgeChatTopK { get; init; }

    public int MaxAllowedKnowledgeChatTopK { get; init; }

    /// <summary>Retries of a model send that failed before its first token. Applies to the next send.</summary>
    public int? ProviderMaxRetries { get; init; }

    public int MinProviderMaxRetries { get; init; }

    public int MaxAllowedProviderMaxRetries { get; init; }

    /// <summary>Sub-agents one run may have in flight at once. Applies to the next run.</summary>
    public int? SpawnMaxConcurrent { get; init; }

    public int MinSpawnMaxConcurrent { get; init; }

    public int MaxAllowedSpawnMaxConcurrent { get; init; }

    /// <summary>Cloud sub-agents one run may start; 0 forbids them. Applies to the next run.</summary>
    public int? SpawnMaxCloud { get; init; }

    public int MinSpawnMaxCloud { get; init; }

    public int MaxAllowedSpawnMaxCloud { get; init; }

    /// <summary>Seconds a sub-agent waits for its busy model; 0 rejects at once. Applies to the next spawn.</summary>
    public int? SpawnQueueWaitSeconds { get; init; }

    public int MinSpawnQueueWaitSeconds { get; init; }

    public int MaxAllowedSpawnQueueWaitSeconds { get; init; }

    /// <summary>Whether a long chat is compacted automatically. Applies to the next turn. Effective value: stored, else the configuration seed.</summary>
    public bool CompactionAutoEnabled { get; init; }

    /// <summary>Whether conversation state is distilled in the background. Applies to the next turn. Effective value: stored, else the configuration seed.</summary>
    public bool CompactionDistillEnabled { get; init; }

    /// <summary>Whether a failed model send is retried before its first token. Applies to the next send. Effective value: stored, else the configuration seed.</summary>
    public bool ProviderRetryEnabled { get; init; }

    /// <summary>Whether a configured reranker runs only for ambiguous candidate sets. Applies to the next search. Effective value: stored, else the configuration seed.</summary>
    public bool KnowledgeAdaptiveRerankingEnabled { get; init; }

    /// <summary>Retrieval latency budget in milliseconds. Applies to the next search.</summary>
    public int? KnowledgeRetrievalLatencyBudgetMs { get; init; }

    public int MinKnowledgeRetrievalLatencyBudgetMs { get; init; }

    public int MaxAllowedKnowledgeRetrievalLatencyBudgetMs { get; init; }

    /// <summary>Whether stale-vector documents are re-queued after an embedding-model change. Applies after a node restart. Effective value: stored, else the configuration seed.</summary>
    public bool KnowledgeScheduledReindexEnabled { get; init; }

    /// <summary>Stale-vector polling interval in minutes. Applies after a node restart.</summary>
    public int? KnowledgeScheduledReindexIntervalMinutes { get; init; }

    public int MinKnowledgeScheduledReindexIntervalMinutes { get; init; }

    public int MaxAllowedKnowledgeScheduledReindexIntervalMinutes { get; init; }

    /// <summary>Whether the knowledge-base agent tools are offered and run. Applies to the next turn. Effective value: stored, else the configuration seed.</summary>
    public bool KnowledgeAgentToolsEnabled { get; init; }

    /// <summary>Whether a cloud-hosted model may receive node-local data. Applies to the next turn. Effective value: stored, else the configuration seed.</summary>
    public bool AllowCloudModelAccess { get; init; }

    /// <summary>Model that proposes playbook actions; blank or absent inherits the default model. Applies to the next run.</summary>
    public string? PlaybookAnalysisModelName { get; init; }

    /// <summary>Model that runs playbook evals; blank or absent inherits the default model. Applies to the next run.</summary>
    public string? PlaybookEvalModelName { get; init; }

    /// <summary>Model that mines lessons from completed runs; blank or absent inherits the default model. Applies to the next run.</summary>
    public string? MemoryExtractionModelName { get; init; }

    /// <summary>Whether old conversations are deleted. Applies to the next sweep. Effective value: stored, else the configuration seed.</summary>
    public bool ChatRetentionEnabled { get; init; }

    /// <summary>Conversation retention window in days. Applies to the next sweep. Effective value: stored, else the configuration seed.</summary>
    public int ChatRetentionDays { get; init; }

    /// <summary>Shared lower bound of the three retention windows, in days.</summary>
    public int MinRetentionDays { get; init; }

    /// <summary>Shared upper bound of the three retention windows, in days.</summary>
    public int MaxAllowedRetentionDays { get; init; }

    /// <summary>Whether old agent execution logs are deleted. Applies to the next sweep. Effective value: stored, else the configuration seed.</summary>
    public bool AgentExecutionLogRetentionEnabled { get; init; }

    /// <summary>Agent execution-log retention window in days. Applies to the next sweep. Effective value: stored, else the configuration seed.</summary>
    public int AgentExecutionLogRetentionDays { get; init; }

    /// <summary>Node database snapshots kept. Applies to the next backup.</summary>
    public int? NodeDbBackupRetainCount { get; init; }

    public int MinNodeDbBackupRetainCount { get; init; }

    public int MaxAllowedNodeDbBackupRetainCount { get; init; }

    /// <summary>Benchmark KL-divergence base cache ceiling in bytes. Applies to the next trim.</summary>
    public long? BenchmarkKldCacheMaxBytes { get; init; }

    public long MinBenchmarkKldCacheMaxBytes { get; init; }

    public long MaxAllowedBenchmarkKldCacheMaxBytes { get; init; }

    /// <summary>Scheduled-job run history window in days. Applies to the next sweep. Effective value: stored, else the configuration seed.</summary>
    public int SchedulerHistoryRetentionDays { get; init; }

    /// <summary>sd-server processes kept loaded at once. Applies after a node restart.</summary>
    public int? ImageMaxLoadedProcesses { get; init; }

    public int MinImageMaxLoadedProcesses { get; init; }

    public int MaxAllowedImageMaxLoadedProcesses { get; init; }

    /// <summary>Whether sd-server keeps the text encoder on the GPU. Applies after a node restart. Effective value: stored, else the configuration seed.</summary>
    public bool ImageTextEncoderOnGpu { get; init; }

    /// <summary>Graph-workflow runs live at once. Applies after a node restart.</summary>
    public int? GraphWorkflowMaxConcurrentRuns { get; init; }

    public int MinGraphWorkflowMaxConcurrentRuns { get; init; }

    public int MaxAllowedGraphWorkflowMaxConcurrentRuns { get; init; }

    /// <summary>Graph-workflow node timeout in seconds when a node names none. Applies after a node restart.</summary>
    public int? GraphWorkflowDefaultNodeTimeoutSeconds { get; init; }

    public int MinGraphWorkflowDefaultNodeTimeoutSeconds { get; init; }

    public int MaxAllowedGraphWorkflowDefaultNodeTimeoutSeconds { get; init; }

    /// <summary>Work-session steps per start or resume. Applies after a node restart.</summary>
    public int? WorkSessionMaxStepsPerRun { get; init; }

    public int MinWorkSessionMaxStepsPerRun { get; init; }

    public int MaxAllowedWorkSessionMaxStepsPerRun { get; init; }

    /// <summary>Work sessions running at once. Applies after a node restart.</summary>
    public int? WorkSessionMaxConcurrentSessions { get; init; }

    public int MinWorkSessionMaxConcurrentSessions { get; init; }

    public int MaxAllowedWorkSessionMaxConcurrentSessions { get; init; }

    /// <summary>Development attempt wall clock in seconds. Applies after a node restart.</summary>
    public int? DevelopmentMaxAttemptDurationSeconds { get; init; }

    public int MinDevelopmentMaxAttemptDurationSeconds { get; init; }

    public int MaxAllowedDevelopmentMaxAttemptDurationSeconds { get; init; }

    /// <summary>Tool calls per development attempt. Applies after a node restart.</summary>
    public int? DevelopmentMaxToolCalls { get; init; }

    public int MinDevelopmentMaxToolCalls { get; init; }

    public int MaxAllowedDevelopmentMaxToolCalls { get; init; }

    /// <summary>Output tokens per development attempt. Applies after a node restart.</summary>
    public int? DevelopmentMaxOutputTokens { get; init; }

    public int MinDevelopmentMaxOutputTokens { get; init; }

    public int MaxAllowedDevelopmentMaxOutputTokens { get; init; }

    /// <summary>AgentHome inner tool calls per run (developer-only). Applies to the next run.</summary>
    public int? AgentHomeMaxInnerToolCalls { get; init; }

    public int MinAgentHomeMaxInnerToolCalls { get; init; }

    public int MaxAllowedAgentHomeMaxInnerToolCalls { get; init; }

    /// <summary>AgentHome patch-apply git timeout in seconds (developer-only). Applies to the next apply.</summary>
    public int? AgentHomePatchApplyTimeoutSeconds { get; init; }

    public int MinAgentHomePatchApplyTimeoutSeconds { get; init; }

    public int MaxAllowedAgentHomePatchApplyTimeoutSeconds { get; init; }

    /// <summary>AgentHome runs kept, 0 = no count limit (developer-only). Applies to the next sweep.</summary>
    public int? AgentHomeRunRetentionMaxRuns { get; init; }

    public int MinAgentHomeRunRetentionMaxRuns { get; init; }

    public int MaxAllowedAgentHomeRunRetentionMaxRuns { get; init; }

    /// <summary>AgentHome runs byte ceiling, 0 = no byte limit (developer-only). Applies to the next sweep.</summary>
    public long? AgentHomeRunRetentionMaxTotalBytes { get; init; }

    public long MinAgentHomeRunRetentionMaxTotalBytes { get; init; }

    public long MaxAllowedAgentHomeRunRetentionMaxTotalBytes { get; init; }

    /// <summary>Whether Development Mode is on. After a node restart for its endpoints, services and hub; the live switch closes them sooner. Effective value: stored, else the configuration seed.</summary>
    public bool DevelopmentEnabled { get; init; }

    /// <summary>Whether work sessions run. Applies to the next request. Effective value: stored, else the configuration seed.</summary>
    public bool WorkSessionsEnabled { get; init; }

    /// <summary>Whether graph workflows run. Applies to the next request and dispatcher tick. Effective value: stored, else the configuration seed.</summary>
    public bool GraphWorkflowsEnabled { get; init; }

    /// <summary>Whether transcription runs. Applies to the next request. Effective value: stored, else the configuration seed.</summary>
    public bool TranscriptionEnabled { get; init; }

    /// <summary>Whether external apps are on. Applies to the next request; the container bridge after a node restart. Effective value: stored, else the configuration seed.</summary>
    public bool ExternalAppsEnabled { get; init; }

    /// <summary>Whether the <c>run_python</c> tool executes. Applies to the next call; the Mathematician agent is seeded on the next restart. Effective value: stored, else the configuration seed.</summary>
    public bool ComputeEnabled { get; init; }

    /// <summary>Whether the AgentHome tools run. Applies to the next call. Effective value: stored, else the configuration seed.</summary>
    public bool AgentHomeEnabled { get; init; }

    /// <summary>Whether the scheduler runs. Applies after a node restart. Effective value: stored, else the configuration seed.</summary>
    public bool SchedulerEnabled { get; init; }

    /// <summary>Whether development workflows run. Applies to the next request and dispatcher tick; definitions are seeded on the next restart. Effective value: stored, else the configuration seed.</summary>
    public bool DevWorkflowsEnabled { get; init; }
}

/// <summary>
///     Body for <c>PUT api/local/v1/node-settings</c>. EVERY field is OPTIONAL, including the chat timeout.
/// </summary>
/// <remarks>
///     A <see langword="null" /> request field keeps the current stored value — the mapper merges into the loaded
///     <see cref="StoredNodeSettings" />. Provided values are validated at the boundary by
///     <see cref="Validators.SaveNodeSettingsRequestValidator" /> (ranges, URL format, tag format, array element constraints).
/// </remarks>
public sealed record SaveNodeSettingsRequest
{
    public int? MaxMessageRequestTimeoutSeconds { get; init; }

    public string? DefaultModelName { get; init; }

    public bool? EnableTools { get; init; }

    /// <summary>
    ///     Node kill-switch for the user-defined custom tools feature. <see langword="null" /> keeps the current
    ///     stored value.
    /// </summary>
    /// <remarks>
    ///     DANGER: enabling this allows agents to run user-defined tools that execute host commands, launch programs
    ///     and make network requests. Off by default; each call still requires operator approval, per the per-agent
    ///     allow-list and the forced per-call approval gate.
    /// </remarks>
    public bool? CustomToolsEnabled { get; init; }

    /// <summary>
    ///     Node switch for the per-turn tool-relevance offer. <see langword="null" /> keeps the current stored value.
    ///     Off by default; a per-agent opt-out still wins when it is on.
    /// </summary>
    public bool? ToolRelevanceEnabled { get; init; }

    /// <summary>
    ///     Node switch for the built-in web tools. <see langword="null" /> keeps the current stored value. Retrieved web
    ///     content is reviewed by the user before it reaches the model; a conversation can auto-accept instead.
    /// </summary>
    public bool? WebAccessEnabled { get; init; }

    /// <summary>
    ///     The SearXNG base URL (absolute http or https) for <c>web_search</c>. <see langword="null" /> keeps the current
    ///     stored value; an empty string clears it, so DuckDuckGo is used again.
    /// </summary>
    public string? WebSearchSearxngUrl { get; init; }

    /// <summary>
    ///     Apply an external-access preset: <c>recommended</c> or <c>offline</c> ONLY.
    /// </summary>
    /// <remarks>
    ///     The server writes that preset's three switches and ignores any switch sent alongside it. <c>custom</c> and
    ///     <c>pending</c> are engine-written states and are REJECTED as input — a client never computes either.
    ///     <see langword="null" /> keeps the current stored value, unless one of the three switches below is supplied,
    ///     which stamps <c>custom</c>.
    /// </remarks>
    public string? ExternalAccessProfile { get; init; }

    /// <summary>
    ///     Set the navigation mode: <c>simple</c> or <c>advanced</c> ONLY; anything else is rejected with a 400.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" /> keeps the current stored value. It composes with no other field, so it can be sent
    ///     on its own — which is how both the first-run step and the Node Settings toggle save it.
    /// </remarks>
    public string? UiMode { get; init; }

    /// <summary>
    ///     Set the update channel: <c>stable</c>, <c>preview</c> or <c>development</c> ONLY; anything else is rejected
    ///     with a 400.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" /> keeps the current stored value. The SPA uses the dedicated
    ///     <c>PUT app-update/channel</c> endpoint instead, which also re-checks immediately; this member exists so the
    ///     MCP and Node Settings surfaces stay consistent with it.
    /// </remarks>
    public string? UpdateChannel { get; init; }

    /// <summary>
    ///     Whether the node checks for application updates on its own.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" /> keeps the current stored value; supplying it (without a preset) stamps the profile
    ///     <c>custom</c>. The manual check and apply flow are unaffected, and the automatic check runs once per
    ///     process, so turning it back on applies at the next start.
    /// </remarks>
    public bool? AutoCheckApplicationUpdates { get; init; }

    /// <summary>
    ///     Whether the node checks for llama.cpp / runtime updates on its own.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" /> keeps the current stored value; supplying it (without a preset) stamps the profile
    ///     <c>custom</c>. The manual runtime-status refresh and the runtime install are unaffected, and the automatic
    ///     check runs once per process.
    /// </remarks>
    public bool? AutoCheckRuntimeUpdates { get; init; }

    /// <summary>
    ///     Whether the node downloads a first-run model and runtime on its own. <see langword="null" /> keeps the current
    ///     stored value; supplying it (without a preset) stamps the profile <c>custom</c>. A manual model download or
    ///     install is unaffected.
    /// </summary>
    public bool? AutoProvisionFirstRunModel { get; init; }

    public IReadOnlyList<string>? ToolCapableModels { get; init; }

    public string? OllamaEndpoint { get; init; }

    public string? HuggingFaceDefaultQuant { get; init; }

    public int? LlamaMaxLoadedProcesses { get; init; }

    public int? LlamaIdleTimeToLiveSeconds { get; init; }

    public bool? KeepModelWarmEnabled { get; init; }

    public string? KeepModelWarmModelName { get; init; }

    public int? KeepModelWarmIntervalSeconds { get; init; }

    public int? MaxResponseSizeMb { get; init; }

    public string? RecommendedLlamaCppTag { get; init; }

    public int? ChatCacheReuse { get; init; }

    public string? SpeculativeMode { get; init; }

    /// <summary>
    ///     KV-cache element type for GPU chat spawns: <c>f16</c> | <c>q8_0</c> | <c>q4_0</c>. Changing it invalidates
    ///     every frozen inference profile on this node.
    /// </summary>
    public string? KvCacheType { get; init; }

    /// <summary>
    ///     Which container runtime External Apps resolves against: <c>auto</c> or <c>docker</c>, case-insensitive.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" /> keeps the current stored value, like every other member of this partial update. A
    ///     STRING, not an enum: a JSON number cannot bind to it, so an undefined selection can never be persisted.
    /// </remarks>
    public string? ContainerRuntimeSelection { get; init; }

    public string? SpeculativeDraftModelName { get; init; }

    public int? SpeculativeDraftMaxTokens { get; init; }

    public int? SpeculativeDraftGpuLayers { get; init; }

    /// <summary>Installed cross-encoder reranker model name for knowledge-base search rerank. Empty/blank disables reranking.</summary>
    public string? RerankerModelName { get; init; }

    public string? AutoEffortFastModelName { get; init; }

    public long? HuggingFaceDiskMarginBytes { get; init; }

    public int? OrchestrationIdleTimeoutSeconds { get; init; }

    public int? AgentHomePrepareTimeoutSeconds { get; init; }

    public int? AgentHomeCommandTimeoutSeconds { get; init; }

    public long? AgentHomeMaxSelectedFolderBytes { get; init; }

    public long? AgentHomeMaxPatchBytes { get; init; }

    public int? MaxPendingToolCallAgeMinutes { get; init; }

    /// <summary>Seconds a run with no attached client keeps going before it is cancelled. <c>0</c> never cancels.</summary>
    public int? DetachedGraceSeconds { get; init; }

    public bool? VoiceFeatureEnabled { get; init; }

    public string? DefaultVoiceProfile { get; init; }

    /// <summary>
    ///     Operator override of usage cost rates, keyed by model NAME to its USD-per-1M input/output rate.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" /> keeps the currently stored override; a supplied map REPLACES it, and an empty map
    ///     clears it because the store's <c>Normalize</c> collapses it to null. Negative or non-finite rates are
    ///     rejected at the boundary with a 400.
    /// </remarks>
    public IReadOnlyDictionary<string, ModelRate>? UsageRates { get; init; }

    /// <summary>Whisper.cpp daemon idle time-to-live in minutes. Applies after a node restart.</summary>
    public int? TranscriptionIdleTimeoutMinutes { get; init; }

    /// <summary>Cap on how long a llama-server may take to become ready, in seconds. Applies after a node restart.</summary>
    public int? LlamaReadinessTimeoutCapSeconds { get; init; }

    /// <summary>Chat-role HTTP timeout to a llama-server, in seconds. Applies after a node restart.</summary>
    public int? LlamaChatHttpTimeoutSeconds { get; init; }

    /// <summary>Embedding/rerank-role HTTP timeout to a llama-server, in seconds. Applies after a node restart.</summary>
    public int? LlamaEmbeddingHttpTimeoutSeconds { get; init; }

    /// <summary>
    ///     Chat-role host prompt-cache budget in MiB; <c>0</c> disables it. <see langword="null" /> keeps the stored value,
    ///     and <c>-1</c> resets it to automatic. Applies after a node restart.
    /// </summary>
    public int? LlamaChatCacheRamMiB { get; init; }

    /// <summary>CPU threads left free for the host when a spawn derives its thread count. Applies after a node restart.</summary>
    public int? LlamaCpuThreadReserve { get; init; }

    /// <summary>Share of GPU memory, in percent, the context allocator keeps free. Applies after a node restart and invalidates frozen inference profiles.</summary>
    public int? LlamaGpuReservePercent { get; init; }

    /// <summary>Share of RAM, in percent, the context allocator keeps free. Applies after a node restart and invalidates frozen inference profiles.</summary>
    public int? LlamaRamReservePercent { get; init; }

    /// <summary>Idle time-to-live of an sd-server daemon, in seconds. Applies after a node restart.</summary>
    public int? ImageIdleTimeToLiveSeconds { get; init; }

    /// <summary>Model-fit safety margin in percent of weights plus KV cache. Applies to the next fit estimate.</summary>
    public int? ModelFitSafetyMarginPercent { get; init; }

    /// <summary>Ceiling on raw provider rounds per invocation. Applies after a node restart.</summary>
    public int? MaxProviderCallsPerInvocation { get; init; }

    /// <summary>Ceiling on a custom command tool's timeout, in seconds. Applies to the next save or call.</summary>
    public int? CustomToolMaxTimeoutSeconds { get; init; }

    /// <summary>Time budget of one web_fetch or web_search call, in seconds. Applies to the next call.</summary>
    public int? WebFetchTimeoutSeconds { get; init; }

    /// <summary>Cap on the readable text one web_fetch returns, in characters. Applies to the next call.</summary>
    public int? WebFetchMaxContentChars { get; init; }

    /// <summary>search_knowledge_base hit count when the model names none; must not exceed the maximum. Applies to the next call.</summary>
    public int? KnowledgeSearchDefaultResults { get; init; }

    /// <summary>Ceiling on the search_knowledge_base hit count. Applies to the next call.</summary>
    public int? KnowledgeSearchMaxResults { get; init; }

    /// <summary>Parallel range connections per large model download. Applies after a node restart.</summary>
    public int? HuggingFaceDownloadConnections { get; init; }

    /// <summary>Whisper.cpp per-request inference timeout, in minutes. Applies after a node restart.</summary>
    public int? TranscriptionInferenceTimeoutMinutes { get; init; }

    /// <summary>AgentHome whole-run time limit in seconds; must be at least the command timeout. Applies to the next run.</summary>
    public int? AgentHomeMaxRunSeconds { get; init; }

    /// <summary>Days an AgentHome run folder is kept. Applies after a node restart.</summary>
    public int? AgentHomeRunRetentionDays { get; init; }

    /// <summary>Tool-loop iterations per request. Applies after a node restart.</summary>
    public int? ToolPipelineMaxIterationsPerRequest { get; init; }

    /// <summary>Characters one tool result may hand the model. Applies after a node restart.</summary>
    public int? ToolPipelineMaxToolResultChars { get; init; }

    /// <summary>Invalid calls to one tool in a row before it is refused. Applies after a node restart.</summary>
    public int? ToolPipelineMaxConsecutiveInvalidToolCalls { get; init; }

    /// <summary>Context window, in tokens, assumed when a send names none. Applies to the next turn.</summary>
    public int? DefaultContextTokens { get; init; }

    /// <summary>Recent messages a provider-round trim always keeps. Applies to the next turn.</summary>
    public int? ProviderBudgetRecentMessagesToKeep { get; init; }

    /// <summary>Input tokens one agent run may send in total. Applies to the next turn.</summary>
    public int? ProviderBudgetMaxCumulativeInputTokens { get; init; }

    /// <summary>Recent turns the history budget never trims. Applies to the next turn.</summary>
    public int? ContextBudgetRecentTurnKeepCount { get; init; }

    /// <summary>Share of the usable window, in percent, above which a chat is compacted. Applies to the next compaction check.</summary>
    public int? CompactionAutoCompactPercent { get; init; }

    /// <summary>Recent messages a compaction keeps word for word. Applies to the next compaction.</summary>
    public int? CompactionRecentMessagesVerbatim { get; init; }

    /// <summary>Attachment text inlined into one chat turn, in characters. Applies to the next turn.</summary>
    public int? MaxInlinedAttachmentChars { get; init; }

    /// <summary>Knowledge-base passages grounded into one chat turn. Applies to the next turn.</summary>
    public int? KnowledgeChatTopK { get; init; }

    /// <summary>Retries of a model send that failed before its first token. Applies to the next send.</summary>
    public int? ProviderMaxRetries { get; init; }

    /// <summary>Sub-agents one run may have in flight at once. Applies to the next run.</summary>
    public int? SpawnMaxConcurrent { get; init; }

    /// <summary>Cloud sub-agents one run may start; 0 forbids them. Applies to the next run.</summary>
    public int? SpawnMaxCloud { get; init; }

    /// <summary>Seconds a sub-agent waits for its busy model; 0 rejects at once. Applies to the next spawn.</summary>
    public int? SpawnQueueWaitSeconds { get; init; }

    /// <summary>Whether a long chat is compacted automatically. Applies to the next turn.</summary>
    public bool? CompactionAutoEnabled { get; init; }

    /// <summary>Whether conversation state is distilled in the background. Applies to the next turn.</summary>
    public bool? CompactionDistillEnabled { get; init; }

    /// <summary>Whether a failed model send is retried before its first token. Applies to the next send.</summary>
    public bool? ProviderRetryEnabled { get; init; }

    /// <summary>Whether a configured reranker runs only for ambiguous candidate sets. Applies to the next search.</summary>
    public bool? KnowledgeAdaptiveRerankingEnabled { get; init; }

    /// <summary>Retrieval latency budget in milliseconds. Applies to the next search.</summary>
    public int? KnowledgeRetrievalLatencyBudgetMs { get; init; }

    /// <summary>Whether stale-vector documents are re-queued after an embedding-model change. Applies after a node restart.</summary>
    public bool? KnowledgeScheduledReindexEnabled { get; init; }

    /// <summary>Stale-vector polling interval in minutes. Applies after a node restart.</summary>
    public int? KnowledgeScheduledReindexIntervalMinutes { get; init; }

    /// <summary>Whether the knowledge-base agent tools are offered and run. Applies to the next turn.</summary>
    public bool? KnowledgeAgentToolsEnabled { get; init; }

    /// <summary>Whether a cloud-hosted model may receive node-local data. Applies to the next turn.</summary>
    public bool? AllowCloudModelAccess { get; init; }

    /// <summary>Model that proposes playbook actions; blank or absent inherits the default model. Applies to the next run.</summary>
    public string? PlaybookAnalysisModelName { get; init; }

    /// <summary>Model that runs playbook evals; blank or absent inherits the default model. Applies to the next run.</summary>
    public string? PlaybookEvalModelName { get; init; }

    /// <summary>Model that mines lessons from completed runs; blank or absent inherits the default model. Applies to the next run.</summary>
    public string? MemoryExtractionModelName { get; init; }

    /// <summary>Whether old conversations are deleted. Applies to the next sweep.</summary>
    public bool? ChatRetentionEnabled { get; init; }

    /// <summary>Conversation retention window in days. Applies to the next sweep.</summary>
    public int? ChatRetentionDays { get; init; }

    /// <summary>Whether old agent execution logs are deleted. Applies to the next sweep.</summary>
    public bool? AgentExecutionLogRetentionEnabled { get; init; }

    /// <summary>Agent execution-log retention window in days. Applies to the next sweep.</summary>
    public int? AgentExecutionLogRetentionDays { get; init; }

    /// <summary>Node database snapshots kept. Applies to the next backup.</summary>
    public int? NodeDbBackupRetainCount { get; init; }

    /// <summary>Benchmark KL-divergence base cache ceiling in bytes. Applies to the next trim.</summary>
    public long? BenchmarkKldCacheMaxBytes { get; init; }

    /// <summary>Scheduled-job run history window in days. Applies to the next sweep.</summary>
    public int? SchedulerHistoryRetentionDays { get; init; }

    /// <summary>sd-server processes kept loaded at once. Applies after a node restart.</summary>
    public int? ImageMaxLoadedProcesses { get; init; }

    /// <summary>Whether sd-server keeps the text encoder on the GPU. Applies after a node restart.</summary>
    public bool? ImageTextEncoderOnGpu { get; init; }

    /// <summary>Graph-workflow runs live at once. Applies after a node restart.</summary>
    public int? GraphWorkflowMaxConcurrentRuns { get; init; }

    /// <summary>Graph-workflow node timeout in seconds when a node names none. Applies after a node restart.</summary>
    public int? GraphWorkflowDefaultNodeTimeoutSeconds { get; init; }

    /// <summary>Work-session steps per start or resume. Applies after a node restart.</summary>
    public int? WorkSessionMaxStepsPerRun { get; init; }

    /// <summary>Work sessions running at once. Applies after a node restart.</summary>
    public int? WorkSessionMaxConcurrentSessions { get; init; }

    /// <summary>Development attempt wall clock in seconds. Applies after a node restart.</summary>
    public int? DevelopmentMaxAttemptDurationSeconds { get; init; }

    /// <summary>Tool calls per development attempt. Applies after a node restart.</summary>
    public int? DevelopmentMaxToolCalls { get; init; }

    /// <summary>Output tokens per development attempt. Applies after a node restart.</summary>
    public int? DevelopmentMaxOutputTokens { get; init; }

    /// <summary>AgentHome inner tool calls per run (developer-only). Applies to the next run.</summary>
    public int? AgentHomeMaxInnerToolCalls { get; init; }

    /// <summary>AgentHome patch-apply git timeout in seconds (developer-only). Applies to the next apply.</summary>
    public int? AgentHomePatchApplyTimeoutSeconds { get; init; }

    /// <summary>AgentHome runs kept, 0 = no count limit (developer-only). Applies to the next sweep.</summary>
    public int? AgentHomeRunRetentionMaxRuns { get; init; }

    /// <summary>AgentHome runs byte ceiling, 0 = no byte limit (developer-only). Applies to the next sweep.</summary>
    public long? AgentHomeRunRetentionMaxTotalBytes { get; init; }

    /// <summary>Whether Development Mode is on. After a node restart for its endpoints, services and hub; the live switch closes them sooner.</summary>
    public bool? DevelopmentEnabled { get; init; }

    /// <summary>Whether work sessions run. Applies to the next request.</summary>
    public bool? WorkSessionsEnabled { get; init; }

    /// <summary>Whether graph workflows run. Applies to the next request and dispatcher tick.</summary>
    public bool? GraphWorkflowsEnabled { get; init; }

    /// <summary>Whether transcription runs. Applies to the next request.</summary>
    public bool? TranscriptionEnabled { get; init; }

    /// <summary>Whether external apps are on. Applies to the next request; the container bridge after a node restart.</summary>
    public bool? ExternalAppsEnabled { get; init; }

    /// <summary>Whether the <c>run_python</c> tool executes. Applies to the next call; the Mathematician agent is seeded on the next restart.</summary>
    public bool? ComputeEnabled { get; init; }

    /// <summary>Whether the AgentHome tools run. Applies to the next call.</summary>
    public bool? AgentHomeEnabled { get; init; }

    /// <summary>Whether the scheduler runs. Applies after a node restart.</summary>
    public bool? SchedulerEnabled { get; init; }

    /// <summary>Whether development workflows run. Applies to the next request and dispatcher tick; definitions are seeded on the next restart.</summary>
    public bool? DevWorkflowsEnabled { get; init; }
}

/// <summary>
///     Body of the 409 <c>PUT api/local/v1/node-settings</c> answers with when the stored record changed under every
///     validation attempt. Nothing was written and nothing the request carried was wrong; a reload and a retry is the
///     whole remedy.
/// </summary>
public sealed record NodeSettingsConflictResponse
{
    public required string Message { get; init; }
}
