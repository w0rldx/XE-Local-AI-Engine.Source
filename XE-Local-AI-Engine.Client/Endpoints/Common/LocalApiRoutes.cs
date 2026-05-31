namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static class LocalApiRoutes
{
    public const string Prefix = "api/local/v1";

    public static class ApiFoundation
    {
        public const string ValidationProblemProbe = "diagnostics/validation-probe";
    }

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

    public static class NodeBinding
    {
        public const string Start = "binding/start";
        public const string Poll = "binding/poll";
        public const string Cancel = "binding/cancel";
    }

    public static class Connection
    {
        public const string Status = "connection";
        public const string Connect = "connection/connect";
        public const string Disconnect = "connection/disconnect";
        public const string EnableAutoConnect = "connection/auto-connect/enable";
        public const string DisableAutoConnect = "connection/auto-connect/disable";
    }

    public static class NodeSettings
    {
        public const string Settings = "node-settings";
    }

    public static class CloudSettings
    {
        public const string Settings = "cloud-settings";
    }

    public static class LocalModels
    {
        public const string Models = "models";
        public const string ModelByName = "models/{modelName}";
        public const string ModelDetails = "models/{modelName}/details";
        public const string Select = "models/select";
        public const string Pull = "models/pull";
    }

    public static class RuntimeManager
    {
        public const string Hub = "/api/local/v1/runtime/hub";
        public const string Status = "runtime/status";
        public const string ContainerAction = "runtime/containers/action";
    }

    public static class Invocations
    {
        public const string Monitor = "invocations";
    }

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

        // Playbook P2: read-only per-agent feedback insights (aggregate over message_feedback). The literal
        // "feedback-insights" segment follows the {agentDefinitionId} param, so it never collides with DefinitionById.
        public const string FeedbackInsights = "agents/{agentDefinitionId}/feedback-insights";
    }

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
