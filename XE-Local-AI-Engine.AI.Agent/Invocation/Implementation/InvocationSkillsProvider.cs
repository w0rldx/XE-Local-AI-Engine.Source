namespace XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>
///     Builds the MAF <c>AgentSkillsProvider</c> that carries resolved skills into an agent as progressive disclosure:
///     the one construction <see cref="InvocationAgentFactory" /> and the sub-agent spawn path share.
/// </summary>
/// <remarks>
///     Instructions and resources only, scripts are never registered. The skills arrive already fenced by the resolver;
///     nothing here decides trust. See docs/wiki/04-agent-mode.md ("Building the agent: instructions once, skills through
///     a context provider" and "4.4 The sub-agent waiver").
/// </remarks>
public static class InvocationSkillsProvider
{
    /// <summary>Builds the skills context provider for an invocation; the caller's agent owns and disposes it.</summary>
    /// <param name="skills">The resolved skills; at least one.</param>
    public static AIContextProvider Create(IReadOnlyList<InvocationSkill> skills) => Build(skills, waiveSkillReadApproval: false);

    /// <summary>Builds the skills context provider for a spawned sub-agent child; the child agent owns and disposes it.</summary>
    /// <remarks>
    ///     Waives the MAF approval on <c>load_skill</c> and <c>read_skill_resource</c>, because a child has no
    ///     human-in-the-loop approval route. <c>run_skill_script</c> keeps its gate.
    /// </remarks>
    /// <param name="skills">The resolved skills; at least one.</param>
    public static AIContextProvider CreateForSubAgentChild(IReadOnlyList<InvocationSkill> skills) =>
        Build(skills, waiveSkillReadApproval: true);

    private static AIContextProvider Build(IReadOnlyList<InvocationSkill> skills, bool waiveSkillReadApproval)
    {
        ArgumentNullException.ThrowIfNull(skills);

        // MAAI001: Agent Skills (AgentSkillsProvider/AgentInlineSkill) shipped as [Experimental] in Microsoft.Agents.AI
        // 1.8.0; the scoped suppression stays at the pinned version until there is explicit graduation evidence.
#pragma warning disable MAAI001
        var inlineSkills = new AgentInlineSkill[skills.Count];
        for (var index = 0; index < skills.Count; index++)
        {
            inlineSkills[index] = BuildInlineSkill(skills[index]);
        }

        return waiveSkillReadApproval
            ? new AgentSkillsProvider(inlineSkills,
                new AgentSkillsProviderOptions
                {
                    DisableLoadSkillApproval = true,
                    DisableReadSkillResourceApproval = true
                })
            : new AgentSkillsProvider(inlineSkills);
#pragma warning restore MAAI001
    }

    /// <summary>
    ///     Builds one MAF <c>AgentInlineSkill</c> from a resolved skill: the full frontmatter constructor plus one
    ///     <c>AddResource</c> per bundled file.
    /// </summary>
    /// <remarks>
    ///     Resources MUST be registered here, before the <c>AgentSkillsProvider</c> is constructed: the provider
    ///     resolves a skill's content once, and a resource added afterwards would exist but never be advertised.
    ///     <c>allowedTools</c> is frontmatter only and grants nothing; scripts are never registered.
    /// </remarks>
    // MAAI001: scoped to the experimental Agent Skills surface, same rationale as the block in Create that calls this.
#pragma warning disable MAAI001
    internal static AgentInlineSkill BuildInlineSkill(InvocationSkill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);

        var inlineSkill = new AgentInlineSkill(skill.Name,
            skill.Description,
            skill.Body,
            license: skill.License,
            compatibility: skill.Compatibility,
            allowedTools: skill.AllowedTools,
            metadata: ToFrontmatterMetadata(skill.Metadata));

        if (skill.Resources is { Count: > 0 } resources)
        {
            foreach (var resource in resources)
            {
                inlineSkill.AddResource(resource.Name, resource.Content, resource.Description);
            }
        }

        return inlineSkill;
    }
#pragma warning restore MAAI001

    /// <summary>Converts the skill's string metadata map onto the loosely-typed dictionary MAF's frontmatter takes.</summary>
    /// <remarks>Null for an absent or empty map, so a skill without metadata keeps the constructor's own default.</remarks>
    private static AdditionalPropertiesDictionary? ToFrontmatterMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        return metadata is { Count: > 0 }
            ? new AdditionalPropertiesDictionary(metadata.Select(static entry => new KeyValuePair<string, object?>(entry.Key, entry.Value)))
            : null;
    }
}
