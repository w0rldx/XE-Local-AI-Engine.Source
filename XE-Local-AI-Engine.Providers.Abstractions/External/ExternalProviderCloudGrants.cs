namespace XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     The operator's per-connection grants of the five cloud permissions to a Cloud-declared external connection.
/// </summary>
/// <remarks>
///     The one rule: a node-wide <c>AllowCloudModel&lt;X&gt;</c> switch is consulted only for a model that leaves the node
///     AND whose connection does not grant X, so a gate withholds when <c>leavesNode &amp;&amp; !grant &amp;&amp; !switch</c>.
///     Grants never change locality: the model stays Cloud for display, usage, the invocation pin and every hard gate.
/// </remarks>
public sealed record ExternalProviderCloudGrants
{
    /// <summary>No grant: every gate falls back to the node-wide switch. The fail-closed value.</summary>
    public static ExternalProviderCloudGrants None { get; } = new();

    /// <summary>Grants node-local data (knowledge, attachments, file tools) like <c>AllowCloudModelAccess</c>.</summary>
    public bool LocalData { get; init; }

    /// <summary>Grants scheduled, graph and integration runs like <c>AllowCloudModelUnattendedRuns</c>.</summary>
    public bool UnattendedRuns { get; init; }

    /// <summary>Grants the web tools like <c>AllowCloudModelWebTools</c>.</summary>
    public bool WebTools { get; init; }

    /// <summary>Grants MCP tools like <c>AllowCloudModelMcpTools</c>.</summary>
    public bool McpTools { get; init; }

    /// <summary>Grants sub-agent spawning like <c>AllowCloudModelSubAgents</c>.</summary>
    public bool SubAgents { get; init; }
}
