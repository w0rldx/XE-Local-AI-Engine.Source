namespace XE_Local_AI_Engine.Client.Endpoints.Common;

/// <summary>
///     Route constants for the node-local HTTP and hub API surface.
/// </summary>
public static class LocalApiRoutes
{
    public const string Prefix = "api/local/v1";

    /// <summary>
    ///     Diagnostic and framework-probe endpoints.
    /// </summary>
    public static class ApiFoundation
    {
        public const string ValidationProblemProbe = "diagnostics/validation-probe";
    }

    /// <summary>
    ///     Node-operator authentication endpoints.
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
    ///     Local chat conversation and streaming routes.
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
    ///     Worker-node binding routes.
    /// </summary>
    public static class NodeBinding
    {
        public const string Start = "binding/start";
        public const string Poll = "binding/poll";
        public const string Cancel = "binding/cancel";
    }

    /// <summary>
    ///     Central-platform connection control routes.
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
    ///     Node settings routes.
    /// </summary>
    public static class NodeSettings
    {
        public const string Settings = "node-settings";
    }

    /// <summary>
    ///     Cloud-provider settings routes.
    /// </summary>
    public static class CloudSettings
    {
        public const string Settings = "cloud-settings";
    }

    /// <summary>
    ///     Local model management routes.
    /// </summary>
    public static class LocalModels
    {
        public const string Models = "models";
        public const string ModelByName = "models/{modelName}";
        public const string ModelDetails = "models/{modelName}/details";
        public const string Select = "models/select";
        public const string Pull = "models/pull";

        // Streaming pull-progress endpoint. Uses the literal "stream" segment after "pull" to keep it distinct from
        // the blocking Pull route, and to prevent the segment from being parsed as a model name route param.
        // Hand-wired on the React client (not in the generated OpenAPI typed client) — mirrors the chat SSE pattern.
        public const string PullStream = "models/pull/stream";

        // Operator override of model classification. The literal "kind" segment keeps this distinct from ModelByName.
        public const string ModelKind = "models/{modelName}/kind";
    }

    /// <summary>
    ///     Runtime manager routes and hub path.
    /// </summary>
    public static class RuntimeManager
    {
        public const string Hub = "/api/local/v1/runtime/hub";
        public const string Status = "runtime/status";
        public const string ContainerAction = "runtime/containers/action";
    }

    /// <summary>
    ///     Invocation monitor routes.
    /// </summary>
    public static class Invocations
    {
        public const string Monitor = "invocations";
    }

    /// <summary>
    ///     Agent definition, playbook, evaluation, and monitoring routes.
    /// </summary>
    public static class Agents
    {
        public const string Definitions = "agents";
        public const string DefinitionById = "agents/{agentDefinitionId}";

        // Distinct literal segment under the agents surface so it cannot collide with DefinitionById.
        public const string ToolCapableModels = "agents/tool-capable-models";

        // Curated starter-pack catalog (GET list) and the operator-triggered import action. Literal segments after
        // "agents" keep these distinct from the {agentDefinitionId} route param.
        public const string Templates = "agents/templates";
        public const string TemplateImport = "agents/templates/import";

        // Per-agent playbook actions nested under the agent definition.
        public const string Playbook = "agents/{agentDefinitionId}/playbook";
        public const string PlaybookActionById = "agents/{agentDefinitionId}/playbook/{actionId}";

        // Analysis and review actions use literal segments so they remain distinct from action-id routes.
        public const string PlaybookAnalyze = "agents/{agentDefinitionId}/playbook/analyze";
        public const string PlaybookActionPromote = "agents/{agentDefinitionId}/playbook/{actionId}/promote";
        public const string PlaybookActionReject = "agents/{agentDefinitionId}/playbook/{actionId}/reject";
        public const string PlaybookActionSuggested = "agents/{agentDefinitionId}/playbook/{actionId}/suggested";

        // Golden-conversation evaluation for a specific suggested playbook action.
        public const string PlaybookActionEval = "agents/{agentDefinitionId}/playbook/{actionId}/eval";

        // Per-agent golden conversation set for manual authoring.
        public const string GoldenConversations = "agents/{agentDefinitionId}/golden-conversations";
        public const string GoldenConversation = "agents/{agentDefinitionId}/golden-conversations/{goldenConversationId}";

        // On-demand thumbs-up harvest and per-candidate approval. Literal action segments keep collection actions
        // distinct from golden-conversation id routes.
        public const string GoldenConversationsHarvest = "agents/{agentDefinitionId}/golden-conversations/harvest";
        public const string GoldenConversationApprove = "agents/{agentDefinitionId}/golden-conversations/{goldenConversationId}/approve";

        // Read-only per-agent feedback insights over message feedback aggregates.
        public const string FeedbackInsights = "agents/{agentDefinitionId}/feedback-insights";

        // Read-only cohort monitoring for enabled playbook actions.
        public const string PlaybookMonitor = "agents/{agentDefinitionId}/playbook/monitor";
    }

    /// <summary>
    ///     Scheduler management, run history, cancellation, and hub routes.
    /// </summary>
    public static class Scheduler
    {
        // Flat template catalog. Kept separate from job-id routes so templates cannot be parsed as ids.
        public const string Templates = "scheduler/templates";

        // Job collection (GET list, POST create) and individual job resource (GET, PUT, DELETE).
        public const string Jobs = "scheduler/jobs";
        public const string JobById = "scheduler/jobs/{scheduledJobId}";

        // Lifecycle actions use literal segments after the job id, keeping action names distinct from JobById.
        public const string JobEnable = "scheduler/jobs/{scheduledJobId}/enable";
        public const string JobDisable = "scheduler/jobs/{scheduledJobId}/disable";
        public const string JobTrigger = "scheduler/jobs/{scheduledJobId}/trigger";

        // Run history uses a flat query-filtered collection plus an individual run resource.
        public const string Runs = "scheduler/runs";
        public const string RunById = "scheduler/runs/{runId}";

        // Cancellation is run-scoped rather than job-scoped; the management service maps it to a Quartz interrupt.
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
    ///     MCP server registration and tool-catalog routes.
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
