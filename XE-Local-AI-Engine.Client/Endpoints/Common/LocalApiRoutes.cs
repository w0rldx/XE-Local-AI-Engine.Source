namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static class LocalApiRoutes
{
    public const string Prefix = "api/local/v1";

    public static class ApiFoundation
    {
        public const string ValidationProblemProbe = "diagnostics/validation-probe";
    }

    public static class LocalChat
    {
        public const string Hub = "/api/local/v1/chat/hub";
        public const string Conversations = "chat/conversations";
        public const string ConversationById = "chat/conversations/{conversationId}";
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
}
