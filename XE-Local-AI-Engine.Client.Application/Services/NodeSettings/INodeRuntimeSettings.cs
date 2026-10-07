namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

using XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>The single read surface migrated consumers use for user-editable runtime knobs.</summary>
/// <remarks>
///     Each getter resolves the effective value with the precedence <c>stored value &gt; appsettings seed &gt; hardcoded default</c>: it reads
///     the cached <see cref="INodeSettingsStore" /> (a sub-millisecond hit after the first load) and falls back to the appsettings seed
///     captured from the bound <c>IOptions&lt;T&gt;</c>/<c>IConfiguration</c> at construction, then to a hardcoded default. Consumers must read
///     migrated values through this surface, never via <c>IOptions&lt;T&gt;</c> of a migrated field — the appsettings binding of a migrated
///     section is the seed only.
/// </remarks>
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

    /// <summary>
    ///     Whether the built-in web tools are enabled (stored &gt; off). Read per offer and per call, so a save applies
    ///     to the next turn without a restart. When off, the web tools are neither offered nor executed.
    /// </summary>
    Task<bool> GetWebAccessEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The operator's SearXNG base URL for <c>web_search</c>, or <see langword="null" /> to use DuckDuckGo. No seed:
    ///     an outbound endpoint has no config-file default.
    /// </summary>
    Task<string?> GetWebSearchSearxngUrlAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Whether the per-turn tool-relevance offer may engage at all (stored &gt; off). Read per turn, so a save
    ///     applies to the next turn without a restart. A per-agent opt-out still wins over this.
    /// </summary>
    Task<bool> GetToolRelevanceEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Which external-access preset was last applied, verbatim and with NO fallback: <see langword="null" /> is the
    ///     answer (nobody has decided), not a missing one.
    /// </summary>
    /// <remarks>
    ///     <c>"pending"</c> means an administrator exists and the choice has not been made. Read only to tell a decided
    ///     node from an undecided one — the three switches below are what every gate reads.
    /// </remarks>
    Task<string?> GetExternalAccessProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Whether the node checks for application updates on its own (stored &gt; on). Gates
    ///     <c>AppUpdateCheckService</c> only; the manual check and apply flow never consult it.
    /// </summary>
    Task<bool> GetAutoCheckApplicationUpdatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Whether the node checks for llama.cpp / runtime updates on its own (stored &gt; on). Gates
    ///     <c>LlamaCppUpdateCheckService</c> only; the manual runtime-status refresh and the runtime install never
    ///     consult it.
    /// </summary>
    Task<bool> GetAutoCheckRuntimeUpdatesAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the node provisions a first-run model on its own (stored &gt; on).</summary>
    /// <remarks>
    ///     Gates <c>FirstRunModelProvisioningService</c>, AFTER the existing <c>FirstRunModel:Enabled</c> config gate
    ///     rather than instead of it; a manual model download or install never consults it.
    /// </remarks>
    Task<bool> GetAutoProvisionFirstRunModelAsync(CancellationToken cancellationToken = default);

    /// <summary>The ceiling on a custom command tool's timeout in seconds (stored &gt; 300). Read per save and per call.</summary>
    Task<int> GetCustomToolMaxTimeoutSecondsAsync(CancellationToken cancellationToken = default);

    /// <summary>The time budget of one <c>web_fetch</c> or <c>web_search</c> call (stored &gt; 20 s).</summary>
    Task<TimeSpan> GetWebFetchTimeoutAsync(CancellationToken cancellationToken = default);

    /// <summary>The cap on the readable text one <c>web_fetch</c> returns, in characters (stored &gt; 12000).</summary>
    Task<int> GetWebFetchMaxContentCharsAsync(CancellationToken cancellationToken = default);

    /// <summary>The <c>search_knowledge_base</c> hit count when the model names none (stored &gt; 5).</summary>
    Task<int> GetKnowledgeSearchDefaultResultsAsync(CancellationToken cancellationToken = default);

    /// <summary>The ceiling on the <c>search_knowledge_base</c> hit count (stored &gt; 20).</summary>
    Task<int> GetKnowledgeSearchMaxResultsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The llama.cpp thinking budget per effort level and the level an unspecified effort takes (each: stored &gt;
    ///     <c>ReasoningBudgets.Default</c>).
    /// </summary>
    Task<ReasoningBudgets> GetReasoningBudgetsAsync(CancellationToken cancellationToken = default);

    /// <summary>The chat output-cap variant and its ceiling (each: stored &gt; <c>cap</c> / 16384).</summary>
    Task<ChatOutputCap> GetChatOutputCapAsync(CancellationToken cancellationToken = default);

    /// <summary>The AgentHome whole-run wall clock in seconds (stored &gt; <c>AgentHome:MaxRunSeconds</c> &gt; 600).</summary>
    Task<int> GetAgentHomeMaxRunSecondsAsync(CancellationToken cancellationToken = default);

    /// <summary>The context window assumed when a send names none (stored &gt; <c>Agent:ProviderCallBudget:DefaultContextTokens</c> &gt; 8192).</summary>
    /// <remarks>
    ///     The <c>Agent:ConversationContextBudget</c> key of the same name is the second seed. One value feeds both the turn budget and
    ///     the provider-round budget; read once per turn.
    /// </remarks>
    Task<int> GetDefaultContextTokensAsync(CancellationToken cancellationToken = default);

    /// <summary>The recent messages a provider-round trim keeps (stored &gt; <c>Agent:ProviderCallBudget:RecentMessagesToKeep</c> &gt; 6).</summary>
    Task<int> GetProviderBudgetRecentMessagesToKeepAsync(CancellationToken cancellationToken = default);

    /// <summary>The total input tokens one run may send (stored &gt; <c>Agent:ProviderCallBudget:MaxCumulativeInputTokens</c> &gt; 4000000).</summary>
    Task<int> GetProviderBudgetMaxCumulativeInputTokensAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether a long chat is compacted after a turn (stored &gt; <c>Agent:ConversationCompaction:AutoCompactEnabled</c> &gt; on).</summary>
    Task<bool> GetCompactionAutoEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The auto-compact threshold in PERCENT of the usable window (stored &gt; <c>Agent:ConversationCompaction:AutoCompactFraction</c>
    ///     × 100 &gt; 75); the consumer divides by 100.
    /// </summary>
    Task<int> GetCompactionAutoCompactPercentAsync(CancellationToken cancellationToken = default);

    /// <summary>The recent messages a compaction keeps verbatim (stored &gt; <c>Agent:ConversationCompaction:RecentMessagesToKeepVerbatim</c> &gt; 8).</summary>
    Task<int> GetCompactionRecentMessagesVerbatimAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether conversation state is distilled (stored &gt; <c>Agent:ConversationCompaction:DistillEnabled</c> &gt; on).</summary>
    Task<bool> GetCompactionDistillEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>The attachment text inlined into one chat turn (stored &gt; <c>Agent:LocalChat:MaxInlinedAttachmentChars</c> &gt; 48000).</summary>
    Task<int> GetMaxInlinedAttachmentCharsAsync(CancellationToken cancellationToken = default);

    /// <summary>The knowledge passages grounded into one chat turn (stored &gt; <c>Agent:LocalChat:KnowledgeChatTopK</c> &gt; 5).</summary>
    Task<int> GetKnowledgeChatTopKAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether a failed model send is retried (stored &gt; <c>Agent:ProviderResilience:RetryEnabled</c> &gt; on).</summary>
    Task<bool> GetProviderRetryEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>The retries of a failed model send (stored &gt; <c>Agent:ProviderResilience:MaxRetries</c> &gt; 2).</summary>
    Task<int> GetProviderMaxRetriesAsync(CancellationToken cancellationToken = default);

    /// <summary>The sub-agents one root run may have in flight (stored &gt; <c>Spawn:MaxConcurrentSpawns</c> &gt; 3).</summary>
    Task<int> GetSpawnMaxConcurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>The cloud sub-agents one root run may start (stored &gt; <c>Spawn:MaxCloudSpawns</c> &gt; 3).</summary>
    Task<int> GetSpawnMaxCloudAsync(CancellationToken cancellationToken = default);

    /// <summary>The wait for a busy sub-agent model, in seconds (stored &gt; <c>Spawn:QueueWaitSeconds</c> &gt; 120).</summary>
    Task<int> GetSpawnQueueWaitSecondsAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether reranking runs only for ambiguous candidate sets (stored &gt; <c>KnowledgeBase:AdaptiveRerankingEnabled</c> &gt; on).</summary>
    Task<bool> GetKnowledgeAdaptiveRerankingEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>The retrieval latency budget in milliseconds (stored &gt; <c>KnowledgeBase:RetrievalLatencyBudgetMilliseconds</c> &gt; 500).</summary>
    Task<int> GetKnowledgeRetrievalLatencyBudgetMsAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the knowledge-base agent tools are offered and run (stored &gt; <c>KnowledgeBase:AgentToolsEnabled</c> &gt; on).</summary>
    Task<bool> GetKnowledgeAgentToolsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Whether a cloud-hosted model may receive node-local data (stored &gt; <c>KnowledgeBase:AllowCloudModelAccess</c> &gt; off). The
    ///     single privacy gate every egress path reads; read per turn.
    /// </summary>
    Task<bool> GetAllowCloudModelAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether unattended runs may use a cloud-hosted model (stored &gt; off); read per run.</summary>
    Task<bool> GetAllowCloudModelUnattendedRunsAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether a cloud-hosted model is offered web and HTTP custom tools (stored &gt; off); read per offer.</summary>
    Task<bool> GetAllowCloudModelWebToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether a cloud-hosted model is offered MCP tools (stored &gt; off); read per offer.</summary>
    Task<bool> GetAllowCloudModelMcpToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether a cloud-hosted model may start sub-agents (stored &gt; off); read per offer and spawn.</summary>
    Task<bool> GetAllowCloudModelSubAgentsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The playbook analysis model (stored &gt; <c>PlaybookAnalysis:ModelName</c> &gt; <c>Ollama:ChatModel</c> &gt; the default model, inherited only when node-local, else <c>Agent:LocalChat:DefaultModel</c>). Read
    ///     once per run.
    /// </summary>
    Task<string> GetPlaybookAnalysisModelNameAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The playbook eval model (stored &gt; <c>PlaybookEval:ModelName</c> &gt; <c>Ollama:ChatModel</c> &gt; the default model, inherited only when node-local, else <c>Agent:LocalChat:DefaultModel</c>). Read once
    ///     per run, so a run's fingerprint and its calls agree.
    /// </summary>
    Task<string> GetPlaybookEvalModelNameAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The memory extraction model (stored &gt; <c>MemoryExtraction:ExtractionModelName</c> &gt; <c>Ollama:ChatModel</c> &gt; the default
    ///     model, inherited only when node-local, else <c>Agent:LocalChat:DefaultModel</c>). Read once per run.
    /// </summary>
    Task<string> GetMemoryExtractionModelNameAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The effective value of every migrated switch and retention window for <paramref name="stored" /> (stored, else the configuration seed). Pure over
    ///     the record passed in, so a save response reports the record it just wrote.
    /// </summary>
    NodeSettingsEffectiveValues ResolveEffectiveValues(StoredNodeSettings stored);

    /// <summary>Whether Development Mode is on (stored &gt; <c>Development:Enabled</c> &gt; on). Live gates only; registration follows <c>NodeStartupSettings</c>.</summary>
    Task<bool> GetDevelopmentEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether work sessions are on (stored &gt; <c>WorkSessions:Enabled</c> &gt; off). Read once per request, turn or tick.</summary>
    Task<bool> GetWorkSessionsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether graph workflows are on (stored &gt; <c>GraphWorkflows:Enabled</c> &gt; on). Read once per request, turn or tick.</summary>
    Task<bool> GetGraphWorkflowsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether transcription is on (stored &gt; <c>Transcription:Enabled</c> &gt; on). Read once per request, turn or tick.</summary>
    Task<bool> GetTranscriptionEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether external apps are on (stored &gt; <c>ExternalApps:Enabled</c> &gt; off). Read once per request, turn or tick.</summary>
    Task<bool> GetExternalAppsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the <c>run_python</c> tool runs (stored &gt; <c>Compute:Enabled</c> &gt; off). Read once per request, turn or tick.</summary>
    Task<bool> GetComputeEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the AgentHome tools are on (stored &gt; <c>AgentHome:Enabled</c> &gt; off). Read once per request, turn or tick.</summary>
    Task<bool> GetAgentHomeEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the scheduler is on (stored &gt; <c>Scheduler:Enabled</c> &gt; on). Read once per request, turn or tick.</summary>
    Task<bool> GetSchedulerEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether development workflows are on (stored &gt; <c>DevWorkflows:Enabled</c> &gt; off). Read once per request, turn or tick.</summary>
    Task<bool> GetDevWorkflowsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Whether Preview sandbox mechanisms may serve a role (stored &gt; <c>ExecutionPreviews:Enabled</c> &gt; off). Read per sandbox
    ///     create and per capability read.
    /// </summary>
    Task<bool> GetExecutionPreviewsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether old conversations are deleted (stored &gt; <c>ChatRetention:Enabled</c> &gt; off). Read per sweep.</summary>
    Task<bool> GetChatRetentionEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>The conversation retention window in days (stored &gt; <c>ChatRetention:RetentionDays</c> &gt; 30).</summary>
    Task<int> GetChatRetentionDaysAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether old agent execution logs are deleted (stored &gt; <c>AgentExecutionLogRetention:Enabled</c> &gt; on). Read per sweep.</summary>
    Task<bool> GetAgentExecutionLogRetentionEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>The agent execution-log retention window in days (stored &gt; <c>AgentExecutionLogRetention:RetentionDays</c> &gt; 30).</summary>
    Task<int> GetAgentExecutionLogRetentionDaysAsync(CancellationToken cancellationToken = default);

    /// <summary>The node database snapshots kept (stored &gt; <c>NodeDbBackup:RetainCount</c> &gt; 3).</summary>
    Task<int> GetNodeDbBackupRetainCountAsync(CancellationToken cancellationToken = default);

    /// <summary>The benchmark KL-divergence base cache ceiling in bytes (stored &gt; <c>Benchmarks:KldCacheMaxBytes</c> &gt; 64 GiB).</summary>
    Task<long> GetBenchmarkKldCacheMaxBytesAsync(CancellationToken cancellationToken = default);

    /// <summary>The scheduled-job run history window in days (stored &gt; <c>Scheduler:HistoryRetentionDays</c> &gt; 30).</summary>
    Task<int> GetSchedulerHistoryRetentionDaysAsync(CancellationToken cancellationToken = default);

    /// <summary>The AgentHome inner tool calls per run (stored &gt; <c>AgentHome:MaxInnerToolCalls</c> &gt; 24). Read once per run.</summary>
    Task<int> GetAgentHomeMaxInnerToolCallsAsync(CancellationToken cancellationToken = default);

    /// <summary>The AgentHome patch-apply git timeout in seconds (stored &gt; <c>AgentHome:PatchApplyTimeoutSeconds</c> &gt; 120).</summary>
    Task<int> GetAgentHomePatchApplyTimeoutSecondsAsync(CancellationToken cancellationToken = default);

    /// <summary>The AgentHome run-folder retention in days, 0 = off (stored &gt; <c>AgentHome:RunRetention:RetentionDays</c> &gt; 30).</summary>
    Task<int> GetAgentHomeRunRetentionDaysAsync(CancellationToken cancellationToken = default);

    /// <summary>The AgentHome runs kept, 0 = off (stored &gt; <c>AgentHome:RunRetention:MaxRuns</c> &gt; 200). Read per sweep.</summary>
    Task<int> GetAgentHomeRunRetentionMaxRunsAsync(CancellationToken cancellationToken = default);

    /// <summary>The AgentHome runs byte ceiling, 0 = off (stored &gt; <c>AgentHome:RunRetention:MaxTotalBytes</c> &gt; 2 GiB). Read per sweep.</summary>
    Task<long> GetAgentHomeRunRetentionMaxTotalBytesAsync(CancellationToken cancellationToken = default);

    // Synchronous twins for the composition/startup path and for structurally synchronous request-time call sites. Prefer the async getters:
    // a sync twin on a per-TOKEN path, or a captured result in a singleton field, is NOT acceptable. Rules and reasons: docs/wiki/08-data-and-persistence.md.

    /// <inheritdoc cref="GetDefaultModelNameAsync" />
    string GetDefaultModelName();

    /// <inheritdoc cref="GetToolCapableModelsAsync" />
    IReadOnlyList<string> GetToolCapableModels();

    /// <summary>The configured <c>AgentHome:ToolCapableModels</c> seed the allowlist falls back to when none is stored. Fixed for the process.</summary>
    IReadOnlyList<string> GetToolCapableModelsSeed();

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

    /// <summary>
    ///     The effective idle time-to-live for the whisper.cpp transcription daemon (stored &gt;
    ///     <c>Transcription:IdleTimeoutMinutes</c> &gt; 15 minutes). Synchronous only: its one caller is the host-build
    ///     factory that seeds the runtime options.
    /// </summary>
    TimeSpan GetTranscriptionIdleTimeout();

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

    // Synchronous only: each of these is read once, by the host-build factory or Configure delegate that seeds a provider
    // option object, so an edit applies on the next node restart.

    /// <summary>The llama-server readiness deadline cap (stored &gt; 600 s).</summary>
    TimeSpan GetLlamaReadinessTimeoutCap();

    /// <summary>The chat-role HTTP timeout to a llama-server (stored &gt; 3600 s).</summary>
    TimeSpan GetLlamaChatHttpTimeout();

    /// <summary>The embedding/rerank-role HTTP timeout to a llama-server (stored &gt; 600 s).</summary>
    TimeSpan GetLlamaEmbeddingHttpTimeout();

    /// <summary>The chat-role <c>--cache-ram</c> budget in MiB, or <see langword="null" /> for the RAM-derived automatic value.</summary>
    int? GetLlamaChatCacheRamMiB();

    /// <summary>The CPU threads left free for the host (stored &gt; 1).</summary>
    int GetLlamaCpuThreadReserve();

    /// <summary>The fraction of GPU memory the context allocator keeps free (stored percent / 100 &gt; 0.05).</summary>
    double GetLlamaGpuReserveFraction();

    /// <summary>The fraction of RAM the context allocator keeps free (stored percent / 100 &gt; 0.15).</summary>
    double GetLlamaRamReserveFraction();

    /// <summary>The sd-server idle time-to-live (stored &gt; <c>StableDiffusionRuntime:IdleTimeToLive</c> &gt; 15 minutes).</summary>
    TimeSpan GetImageIdleTimeToLive();

    /// <summary>The raw provider-round ceiling per invocation (stored &gt; <c>Agent:ProviderCallBudget:MaxProviderCallsPerInvocation</c> &gt; 200).</summary>
    int GetMaxProviderCallsPerInvocation();

    /// <summary>The parallel range connections per large model download (stored &gt; <c>HuggingFace:DownloadConnections</c> &gt; 4).</summary>
    int GetHuggingFaceDownloadConnections();

    /// <summary>The whisper.cpp per-request inference timeout (stored &gt; 30 minutes).</summary>
    TimeSpan GetTranscriptionInferenceTimeout();

    /// <summary>
    ///     The model-fit safety margin as a fraction of weights + KV (stored percent / 100 &gt; 0.12). Synchronous
    ///     because the estimator it feeds is synchronous; it is read per estimate, so a save applies to the next fit.
    /// </summary>
    double GetModelFitSafetyMarginFraction();

    /// <summary>The tool-loop iterations per request (stored &gt; <c>Agent:ToolPipeline:MaximumToolIterationsPerRequest</c> &gt; 40).</summary>
    int GetToolPipelineMaxIterationsPerRequest();

    /// <summary>The characters one tool result may hand the model (stored &gt; <c>Agent:ToolPipeline:MaxToolResultCharacters</c> &gt; 65536).</summary>
    int GetToolPipelineMaxToolResultChars();

    /// <summary>The invalid calls to one tool in a row before it is refused (stored &gt; <c>Agent:ToolPipeline:MaxConsecutiveInvalidToolCallsPerTool</c> &gt; 3).</summary>
    int GetToolPipelineMaxConsecutiveInvalidToolCalls();

    /// <summary>The recent turns the history budget never trims (stored &gt; <c>Agent:ConversationContextBudget:RecentTurnKeepCount</c> &gt; 4).</summary>
    /// <remarks>
    ///     Synchronous because <c>IConversationContextBudgeter.Budget</c> is synchronous by design; it is read per budget pass, never
    ///     captured, so a save applies to the next turn.
    /// </remarks>
    int GetContextBudgetRecentTurnKeepCount();

    /// <summary>Whether stale-vector documents are re-queued (stored &gt; <c>KnowledgeBase:ScheduledModelReindexEnabled</c> &gt; on).</summary>
    bool GetKnowledgeScheduledReindexEnabled();

    /// <summary>The stale-vector polling interval in minutes (stored &gt; <c>KnowledgeBase:ScheduledModelReindexIntervalMinutes</c> &gt; 60).</summary>
    int GetKnowledgeScheduledReindexIntervalMinutes();

    /// <summary>
    ///     Synchronous twin of <see cref="GetKnowledgeAgentToolsEnabledAsync" /> for the synchronous tool-offer seam; read per offer,
    ///     never captured.
    /// </summary>
    bool GetKnowledgeAgentToolsEnabled();

    /// <summary>
    ///     Synchronous twin of <see cref="GetAllowCloudModelAccessAsync" /> for the synchronous tool-offer seam; read per offer, never
    ///     captured.
    /// </summary>
    bool GetAllowCloudModelAccess();

    /// <summary>Synchronous twin of <see cref="GetAllowCloudModelUnattendedRunsAsync" />; read per call, never captured.</summary>
    bool GetAllowCloudModelUnattendedRuns();

    /// <summary>Synchronous twin of <see cref="GetAllowCloudModelWebToolsAsync" /> for the synchronous tool-offer seam; never captured.</summary>
    bool GetAllowCloudModelWebTools();

    /// <summary>Synchronous twin of <see cref="GetAllowCloudModelMcpToolsAsync" /> for the synchronous tool-offer seam; never captured.</summary>
    bool GetAllowCloudModelMcpTools();

    /// <summary>Synchronous twin of <see cref="GetAllowCloudModelSubAgentsAsync" /> for the synchronous spawn-offer seam; never captured.</summary>
    bool GetAllowCloudModelSubAgents();

    /// <summary>The sd-server processes kept loaded (stored &gt; <c>StableDiffusionRuntime:MaxLoadedProcesses</c> &gt; 1).</summary>
    int GetImageMaxLoadedProcesses();

    /// <summary>Whether sd-server keeps the text encoder on the GPU (stored &gt; <c>StableDiffusionRuntime:TextEncoderOnGpu</c> &gt; off).</summary>
    bool GetImageTextEncoderOnGpu();

    /// <summary>The graph-workflow runs live at once (stored &gt; <c>GraphWorkflows:MaxConcurrentRuns</c> &gt; 4).</summary>
    int GetGraphWorkflowMaxConcurrentRuns();

    /// <summary>The graph-workflow default node timeout in seconds (stored &gt; <c>GraphWorkflows:DefaultNodeTimeoutSeconds</c> &gt; 600).</summary>
    int GetGraphWorkflowDefaultNodeTimeoutSeconds();

    /// <summary>The work-session steps per start or resume (stored &gt; <c>WorkSessions:MaxStepsPerRun</c> &gt; 25).</summary>
    int GetWorkSessionMaxStepsPerRun();

    /// <summary>The work sessions running at once (stored &gt; <c>WorkSessions:MaxConcurrentSessions</c> &gt; 1).</summary>
    int GetWorkSessionMaxConcurrentSessions();

    /// <summary>The development attempt wall clock in seconds (stored &gt; <c>Development:MaxAttemptDurationSeconds</c> &gt; 1800).</summary>
    int GetDevelopmentMaxAttemptDurationSeconds();

    /// <summary>The tool calls per development attempt (stored &gt; <c>Development:MaxToolCalls</c> &gt; 64).</summary>
    int GetDevelopmentMaxToolCalls();

    /// <summary>The output tokens per development attempt (stored &gt; <c>Development:MaxOutputTokens</c> &gt; 32768).</summary>
    int GetDevelopmentMaxOutputTokens();

    // The feature switches, for the synchronous sites: an options overlay, a seeder read once at start, a synchronous capacity check.
    bool GetDevelopmentEnabled();

    bool GetWorkSessionsEnabled();

    bool GetGraphWorkflowsEnabled();

    bool GetTranscriptionEnabled();

    bool GetExternalAppsEnabled();

    bool GetComputeEnabled();

    bool GetAgentHomeEnabled();

    bool GetSchedulerEnabled();

    bool GetDevWorkflowsEnabled();

    bool GetExecutionPreviewsEnabled();
}
