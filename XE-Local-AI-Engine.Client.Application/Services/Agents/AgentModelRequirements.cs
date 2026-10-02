namespace XE_Local_AI_Engine.Client.Services.Agents;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     What a saved agent needs from its model, derived from the definition rather than declared on it, and the one
///     refusal sentence every unattended surface uses when the effective model cannot meet it.
/// </summary>
/// <remarks>
///     Tools only: image input is already gated per surface (chat withholds attachments, a graph node refuses them).
///     Interactive chat runs anyway and shows a ToolsWithheld notice; the scheduler, graph Agent nodes, integrations and
///     inbound MCP refuse before capacity, because nobody is there to see a run that silently lost its tools.
/// </remarks>
public static class AgentModelRequirements
{
    /// <summary>
    ///     An agent requires tool calling when it lists tools, or when it is an orchestrator, whose handoffs are tool
    ///     calls. The seeded Default Assistant lists none and requires nothing.
    /// </summary>
    public static bool RequiresTools(IReadOnlyCollection<string> allowedToolNames, AgentDefinitionKind kind)
    {
        ArgumentNullException.ThrowIfNull(allowedToolNames);
        return allowedToolNames.Count > 0 || kind == AgentDefinitionKind.Orchestrator;
    }

    /// <summary>The operator-safe refusal for a tool-requiring agent on a model without tool calling, else <see langword="null" />.</summary>
    public static string? ToolRefusal(string agentName, string effectiveModel, bool supportsTools, bool requiresTools) =>
        requiresTools && !supportsTools
            ? $"The agent '{agentName}' needs tool calling, which its model '{effectiveModel}' is not known to support, so it will not run unattended; pick a tool-capable model or remove the agent's tools."
            : null;
}
