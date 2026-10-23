namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    public static class Mcp
    {
        public const string Servers = "mcp/servers";
        public const string ServerById = "mcp/servers/{mcpServerId}";
        public const string ServerEnabled = "mcp/servers/{mcpServerId}/enabled";
        public const string ServerTools = "mcp/servers/{mcpServerId}/tools";

        // The full dynamic tool catalog (built-ins + enabled MCP tools). A distinct top-level literal so it never
        // collides with the {mcpServerId} route param under the servers surface.
        public const string ToolCatalog = "tool-catalog";

        // Inbound MCP server routes; preceding routes are outbound MCP client routes.

        /// <summary>
        ///     Operator-gated management of the single inbound bearer credential (GET reveal / POST generate /
        ///     DELETE revoke). A literal segment under <c>mcp/</c>, so it can never be parsed as an {mcpServerId}.
        /// </summary>
        public const string ServerApiKey = "mcp/server-key";

        /// <summary>
        ///     The MCP Streamable HTTP endpoint itself, mapped by <c>MapMcp</c> OUTSIDE FastEndpoints (like the SignalR
        ///     hubs) but deliberately INSIDE the <c>/api/local/v1</c> prefix.
        /// </summary>
        /// <remarks>
        ///     <c>LocalApiSecurityMiddleware</c>'s loopback peer + Host + Origin gate therefore still covers it. Moving
        ///     it outside the prefix would silently drop that gate and leave the bearer key as the only control.
        /// </remarks>
        public const string ServerEndpoint = "mcp/server";
    }
}
