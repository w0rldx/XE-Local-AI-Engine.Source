namespace XE_Local_AI_Engine.Cli.Commands;

using XE_Local_AI_Engine.Cli.Api;

/// <summary>Resolves <c>--agent</c> and <c>/agent</c>: an agent id as is, or a unique name, case-insensitive.</summary>
internal static class AgentLookup
{
    public static async Task<AgentDefinition?> ResolveAsync(NodeApiClient api, string? value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(api);

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var agents = (await api.ListAgentsAsync(cancellationToken)).Items;
        var matches = Guid.TryParse(value, out var id)
            ? agents.Where(agent => agent.Id == id).ToList()
            : agents.Where(agent => string.Equals(agent.Name, value.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CliCommandException(ExitCodes.Usage, $"No agent '{value}' on this node. Run 'xe agents' to list them."),
            _ => throw new CliCommandException(ExitCodes.Usage, $"Several agents are named '{value}'. Pass the agent id instead.")
        };
    }

    public static Guid ParseConversationId(string value) =>
        Guid.TryParse(value, out var id)
            ? id
            : throw new CliCommandException(ExitCodes.Usage, $"'{value}' is not a conversation id. Run 'xe chat' and /conversations to list them.");
}
