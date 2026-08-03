namespace XE_Local_AI_Engine.Client.DependencyInjection;

using System.Reflection;
using ModelContextProtocol.Protocol;
using XE_Local_AI_Engine.Client.Services.Mcp.Server;

/// <summary>
///     Registers this node's INBOUND MCP server — the surface an external MCP client (Claude Code, Claude Desktop, an
///     IDE) connects to in order to delegate work to the local model. The OUTBOUND direction (this node connecting to
///     third-party MCP servers) is registered separately in <c>AddNodeModelCapabilitiesAndMcpExtensions</c>.
/// </summary>
internal static class AddNodeMcpServerExtensions
{
    /// <summary>
    ///     The node version reported to MCP clients as server identity. Read from the informational version attribute so
    ///     it tracks the shipped build rather than a literal that would silently go stale.
    /// </summary>
    private static string ServerVersion =>
        typeof(AddNodeMcpServerExtensions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AddNodeMcpServerExtensions).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    public static IHostApplicationBuilder AddNodeMcpServer(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder.Services
                   .AddMcpServer(options =>
                   {
                       options.ServerInfo = new Implementation
                       {
                           Name = "xe-local-ai-engine",
                           Title = "XE Local AI Engine",
                           Version = ServerVersion
                       };
                   })
                   .WithHttpTransport(transport =>
                   {
                       // Stateless is the 2026-07-28 default (SEP-2567 removed protocol-level sessions). Held explicitly
                       // rather than left implicit because it is load-bearing here: a stateless server keeps no
                       // per-connection state, so an MCP client reconnecting mid-run cannot resume into a half-applied
                       // session, and there is no session table to bound or evict.
                       transport.Stateless = true;
                   })
                   .WithTools<NodeAgentMcpTools>();

        return builder;
    }
}
