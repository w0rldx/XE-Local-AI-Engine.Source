namespace XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     Shared constants for the node-local default chat persona. The <see cref="DefaultAgentSeedSlug" /> is the single
///     source of truth for the seeded "Default Assistant" definition: the seeder mints it idempotently, the resolver
///     grants it the full capability-gated tool offer (reproducing today's chat), and the stream/regeneration services
///     fall back to it when no agent is selected on the send.
/// </summary>
public static class AgentDefaults
{
    /// <summary>The forge-proof seed slug of the node-local "Default Assistant" definition (mode-off persona).</summary>
    public const string DefaultAgentSeedSlug = "default-assistant";

    /// <summary>The data-row display name of the seeded "Default Assistant" definition (not localized).</summary>
    public const string DefaultAgentName = "Default Assistant";
}
