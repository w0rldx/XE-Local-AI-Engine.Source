namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>Operator-managed opaque workspace allowlist for inbound MCP delegation.</summary>
    public static class Workspaces
    {
        public const string Collection = "workspaces";
        public const string ById = "workspaces/{workspaceId}";
    }
}
