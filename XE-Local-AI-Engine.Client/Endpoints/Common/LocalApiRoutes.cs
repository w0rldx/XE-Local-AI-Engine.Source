namespace XE_Local_AI_Engine.Client.Endpoints.Common;

/// <summary>
///     Local API contract type for local api routes.
/// </summary>
public static class LocalApiRoutes
{
    public const string Prefix = "api/local/v1";

    /// <summary>
    ///     Local API contract type for api foundation.
    /// </summary>
    public static class ApiFoundation
    {
        public const string ValidationProblemProbe = "diagnostics/validation-probe";
    }

    /// <summary>
    ///     Local API contract type for auth.
    /// </summary>
    public static class Auth
    {
        public const string Status = "auth/status";
        public const string Setup = "auth/setup";
        public const string Login = "auth/login";
        public const string Refresh = "auth/refresh";
        public const string Logout = "auth/logout";
        public const string ChangePassword = "auth/change-password";
        public const string Me = "auth/me";
    }

    /// <summary>
    ///     Local API contract type for local chat.
    /// </summary>
    public static class LocalChat
    {
        public const string Hub = "/api/local/v1/chat/hub";
        public const string Conversations = "chat/conversations";
        public const string ConversationById = "chat/conversations/{conversationId}";
        public const string RenameConversation = "chat/conversations/{conversationId}/rename";
        public const string PinConversation = "chat/conversations/{conversationId}/pin";
        public const string ArchiveConversation = "chat/conversations/{conversationId}/archive";
        public const string BranchConversation = "chat/conversations/{conversationId}/branch/{messageId}";
        public const string MessageRevisions = "chat/conversations/{conversationId}/messages/{messageId}/revisions";
        public const string MessageFeedback = "chat/conversations/{conversationId}/messages/{messageId}/feedback";
        public const string SelectedPath = "chat/conversations/{conversationId}/selected-path";
        public const string Cancel = "chat/cancel";
    }

    /// <summary>
    ///     Local API contract type for node binding.
    /// </summary>
    public static class NodeBinding
    {
        public const string Start = "binding/start";
        public const string Poll = "binding/poll";
        public const string Cancel = "binding/cancel";
    }

    /// <summary>
    ///     Local API contract type for connection.
    /// </summary>
    public static class Connection
    {
        public const string Status = "connection";
        public const string Connect = "connection/connect";
        public const string Disconnect = "connection/disconnect";
        public const string EnableAutoConnect = "connection/auto-connect/enable";
        public const string DisableAutoConnect = "connection/auto-connect/disable";
    }

    /// <summary>
    ///     Local API contract type for node settings.
    /// </summary>
    public static class NodeSettings
    {
        public const string Settings = "node-settings";
    }

    /// <summary>
    ///     Local API contract type for cloud settings.
    /// </summary>
    public static class CloudSettings
    {
        public const string Settings = "cloud-settings";
    }

    /// <summary>
    ///     Local API contract type for local models.
    /// </summary>
    public static class LocalModels
    {
        public const string Models = "models";
        public const string ModelByName = "models/{modelName}";
        public const string ModelDetails = "models/{modelName}/details";
        public const string Select = "models/select";
        public const string Pull = "models/pull";

        // Operator override of a model's classification. The literal "kind" segment follows the {modelName} param, so
        // FastEndpoints prioritises it over ModelByName's bare param route (same literal-vs-param rule as ModelDetails).
        public const string ModelKind = "models/{modelName}/kind";
    }

    /// <summary>
    ///     Local API contract type for runtime manager.
    /// </summary>
    public static class RuntimeManager
    {
        public const string Hub = "/api/local/v1/runtime/hub";
        public const string Status = "runtime/status";
        public const string ContainerAction = "runtime/containers/action";
    }

    /// <summary>
    ///     Local API contract type for invocations.
    /// </summary>
    public static class Invocations
    {
        public const string Monitor = "invocations";
    }

    /// <summary>
    ///     Local API contract type for agents.
    /// </summary>
    public static class Agents
    {
        public const string Definitions = "agents";
        public const string DefinitionById = "agents/{agentDefinitionId}";

        // A distinct literal segment under the agents surface; FastEndpoints prioritises the literal over the
        // {agentDefinitionId} route param so this never collides with DefinitionById.
        public const string ToolCapableModels = "agents/tool-capable-models";

        // Playbook P1: per-agent playbook actions nested under the agent. The literal "playbook" segment follows the
        // {agentDefinitionId} param, so it never collides with DefinitionById (which has no trailing segment).
        public const string Playbook = "agents/{agentDefinitionId}/playbook";
        public const string PlaybookActionById = "agents/{agentDefinitionId}/playbook/{actionId}";

        // Playbook P3: analysis-agent staging. "analyze" is a literal segment after "playbook", so FastEndpoints
        // prioritises it over PlaybookActionById's {actionId} param (same literal-vs-param rule as ToolCapableModels).
        // promote/reject/suggested are literal segments after {actionId}, so they never collide with PlaybookActionById.
        public const string PlaybookAnalyze = "agents/{agentDefinitionId}/playbook/analyze";
        public const string PlaybookActionPromote = "agents/{agentDefinitionId}/playbook/{actionId}/promote";
        public const string PlaybookActionReject = "agents/{agentDefinitionId}/playbook/{actionId}/reject";
        public const string PlaybookActionSuggested = "agents/{agentDefinitionId}/playbook/{actionId}/suggested";

        // Playbook P4: eval gate. "eval" is a literal segment after {actionId}, so it never collides with
        // PlaybookActionById (same literal-vs-param rule as promote/reject above).
        public const string PlaybookActionEval = "agents/{agentDefinitionId}/playbook/{actionId}/eval";

        // Playbook P4: per-agent golden conversation set (manual authoring). The literal "golden-conversations"
        // segment follows the {agentDefinitionId} param, so it never collides with DefinitionById.
        public const string GoldenConversations = "agents/{agentDefinitionId}/golden-conversations";
        public const string GoldenConversation = "agents/{agentDefinitionId}/golden-conversations/{goldenConversationId}";

        // Harvest follow-up: on-demand thumbs-up harvest + per-candidate approve. The literal "harvest" segment follows
        // "golden-conversations", so FastEndpoints prioritises it over the {goldenConversationId} param (same literal-vs-param
        // rule as ToolCapableModels); "approve" is a literal segment after {goldenConversationId}, so it never collides with
        // GoldenConversation's bare param route (the DELETE / GET-by-id surface).
        public const string GoldenConversationsHarvest = "agents/{agentDefinitionId}/golden-conversations/harvest";
        public const string GoldenConversationApprove = "agents/{agentDefinitionId}/golden-conversations/{goldenConversationId}/approve";

        // Playbook P2: read-only per-agent feedback insights (aggregate over message_feedback). The literal
        // "feedback-insights" segment follows the {agentDefinitionId} param, so it never collides with DefinitionById.
        public const string FeedbackInsights = "agents/{agentDefinitionId}/feedback-insights";

        // Playbook P5: read-only cohort monitoring for an agent's Enabled playbook actions. The literal "monitor"
        // segment follows the literal "playbook" segment, so it never collides with PlaybookActionById's {actionId}
        // param (same literal-vs-param rule as PlaybookAnalyze).
        public const string PlaybookMonitor = "agents/{agentDefinitionId}/playbook/monitor";
    }

    /// <summary>
    ///     Local API contract type for scheduler.
    /// </summary>
    public static class Scheduler
    {
        // Flat template catalog — a distinct literal so it never collides with the {scheduledJobId} param on Jobs.
        public const string Templates = "scheduler/templates";

        // Job collection (GET list, POST create) and individual job resource (GET, PUT, DELETE).
        public const string Jobs = "scheduler/jobs";
        public const string JobById = "scheduler/jobs/{scheduledJobId}";

        // Lifecycle actions — literal segments after the {scheduledJobId} param; FastEndpoints prioritises literals
        // over route params so these never collide with JobById (same pattern as playbook promote/reject).
        public const string JobEnable = "scheduler/jobs/{scheduledJobId}/enable";
        public const string JobDisable = "scheduler/jobs/{scheduledJobId}/disable";
        public const string JobTrigger = "scheduler/jobs/{scheduledJobId}/trigger";

        // Run history — flat collection (query-filtered) and individual run.
        public const string Runs = "scheduler/runs";
        public const string RunById = "scheduler/runs/{runId}";

        // Cancel a running job run — literal segment after the {runId} param (literals prioritised over route params).
        public const string RunCancel = "scheduler/runs/{runId}/cancel";

        // SignalR push hub for scheduler lifecycle events. Full path (mapped via MapHub, not the FastEndpoints prefix),
        // mirroring LocalChat.Hub / RuntimeManager.Hub.
        public const string Hub = "/api/local/v1/scheduler/hub";
    }

    /// <summary>
    ///     Local API contract type for model-fit (llmfit approved-image recommendations). Cache-first: the latest
    ///     endpoints read the cached snapshot and never run llmfit; the refresh endpoint delegates to the scheduler
    ///     trigger and never executes llmfit directly. Benchmark routes are intentionally not exposed yet.
    /// </summary>
    public static class ModelFit
    {
        // Read-only approved utility image registry projection.
        public const string ApprovedImages = "model-fit/approved-images";

        // Latest cached recommendation snapshot (query-filtered by useCase/providerName). The literal "latest" segment
        // follows "recommendations", so it never collides with the "refresh" action below.
        public const string RecommendationsLatest = "model-fit/recommendations/latest";

        // Manual refresh trigger — a template-guarded facade over the scheduler trigger service. The literal "refresh"
        // segment follows "recommendations", so it never collides with "latest".
        public const string RecommendationsRefresh = "model-fit/recommendations/refresh";
    }

    /// <summary>
    ///     Local API contract type for mcp.
    /// </summary>
    public static class Mcp
    {
        public const string Servers = "mcp/servers";
        public const string ServerById = "mcp/servers/{mcpServerId}";
        public const string ServerEnabled = "mcp/servers/{mcpServerId}/enabled";
        public const string ServerTools = "mcp/servers/{mcpServerId}/tools";

        // The full dynamic tool catalog (built-ins + enabled MCP tools). A distinct top-level literal so it never
        // collides with the {mcpServerId} route param under the servers surface.
        public const string ToolCatalog = "tool-catalog";
    }
}
