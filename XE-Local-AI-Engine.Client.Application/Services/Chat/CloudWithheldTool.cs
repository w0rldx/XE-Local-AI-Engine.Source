namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     A tool a cloud-model switch removed from the offer pool of a model that leaves the node, and the switch that did.
/// </summary>
public sealed record CloudWithheldTool
{
    public required string Name { get; init; }

    public required CloudToolSwitch Switch { get; init; }
}

/// <summary>The three per-function cloud-model switches that withhold a tool class from a model that leaves the node.</summary>
public enum CloudToolSwitch
{
    /// <summary><c>AllowCloudModelMcpTools</c>: every tool of a connected MCP server.</summary>
    McpTools,

    /// <summary><c>AllowCloudModelWebTools</c>: the built-in web tools and the HttpFetch custom tools.</summary>
    WebTools,

    /// <summary><c>AllowCloudModelSubAgents</c>: <c>spawn_subagent</c>.</summary>
    SubAgents
}
