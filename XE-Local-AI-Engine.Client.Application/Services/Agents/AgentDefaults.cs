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

    /// <summary>
    ///     The forge-proof seed slug of the node-local "Coder (read-only)" definition — a read-only project-access agent
    ///     carrying the three coder tool names in its <c>AllowedToolNames</c>.
    /// </summary>
    public const string CoderAgentSeedSlug = "coder-readonly";

    /// <summary>The data-row display name of the seeded "Coder (read-only)" definition (not localized).</summary>
    public const string CoderAgentName = "Coder (read-only)";

    /// <summary>
    ///     The forge-proof seed slug of the general work-session persona: the four state tools plus <c>ask_user</c> and
    ///     the clock.
    /// </summary>
    public const string WorkSessionGeneralAgentSeedSlug = "work-session-general";

    /// <summary>The data-row display name of the seeded general work-session definition (not localized).</summary>
    public const string WorkSessionGeneralAgentName = "Work Session — General";

    /// <summary>The forge-proof seed slug of the research work-session persona: the general set plus the knowledge-base reads.</summary>
    public const string WorkSessionResearchAgentSeedSlug = "work-session-research";

    /// <summary>The data-row display name of the seeded research work-session definition (not localized).</summary>
    public const string WorkSessionResearchAgentName = "Work Session — Research";
}
