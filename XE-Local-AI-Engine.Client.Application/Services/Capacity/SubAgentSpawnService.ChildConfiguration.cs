namespace XE_Local_AI_Engine.Client.Services.Capacity;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;

internal sealed partial class SubAgentSpawnService
{
    // Bridges the resolver's curated AllowedTools (capability-gated, profile-pool offer ∩ AllowedToolNames) to
    // executables via the shared InvocationToolResolver, then filters spawn_subagent out (the structural depth cap).
    private IList<AITool>? CurateChildTools(IReadOnlyList<AllowedToolDto> allowedTools)
    {
        if (allowedTools.Count == 0)
        {
            return null;
        }

        var offeredExecutables = InvocationToolResolver.Resolve(SubAgentSpawnPolicy.ToOfferPlaceholders(allowedTools),
            _toolRegistry,
            _clientLocalToolRegistry,
            _mcpToolRegistry,
            _logger);

        // Two unconditional strips, both structural: spawn_subagent — the DEPTH CAP, mirrored by the runtime Depth guard — and any ApprovalRequiredAIFunction,
        // because a child run via AsAIFunction has NO HITL ROUTE and fails every such call silently. Dropped and warned by name, never unwrapped to auto-execute.
        var curated = SubAgentSpawnPolicy.RemoveUnsupportedChildTools(offeredExecutables, out var droppedApprovalTools);
        if (droppedApprovalTools.Count > 0)
        {
            _logger.LogWarning("Dropped {DroppedCount} approval-required tool(s) from a sub-agent child ({DroppedTools}); a spawned child has no human-in-the-loop approval route.",
                droppedApprovalTools.Count,
                string.Join(", ", droppedApprovalTools));
        }

        return curated;
    }

    /// <summary>
    ///     Builds a MAF <c>AgentSkillsProvider</c> from the resolved node skills (frontmatter, body-as-instructions, bundled resources; never
    ///     scripts) and attaches it to the child's options, mirroring <c>InvocationAgentFactory.BuildAgent</c>.
    /// </summary>
    /// <remarks>
    ///     Empty or null is a no-op, so a no-skills child stays byte-identical. The child receives the parent's ALREADY-RESOLVED skill set, so
    ///     an imported skill arrives fenced: that trust decision is taken once at the resolver and is neither re-taken nor reversed here.
    ///     <c>load_skill</c> and <c>read_skill_resource</c> have their MAF approval waived for children and only for children, because these
    ///     tools arrive through <c>AIContextProviders</c> and so bypass <c>CurateChildTools</c>; <c>run_skill_script</c> keeps its gate. See
    ///     <c>docs/wiki/04-agent-mode.md</c> ("4.4 The sub-agent waiver").
    /// </remarks>
    private static void AttachSkillsProvider(ChatClientAgentOptions agentOptions,
        IReadOnlyList<ResolvedSkill>? skills,
        ILogger<SubAgentSpawnService> logger)
    {
        if (skills is not { Count: > 0 } resolvedSkills)
        {
            return;
        }

        // MAAI001: Agent Skills (AgentSkillsProvider/AgentInlineSkill) shipped [Experimental] in Microsoft.Agents.AI 1.8.0, so the scoped suppression
        // stays at the pinned version (Directory.Packages.props) until explicit graduation evidence — the same scoped suppression InvocationAgentFactory uses.
#pragma warning disable MAAI001
        var inlineSkills = new AgentInlineSkill[resolvedSkills.Count];
        for (var index = 0; index < resolvedSkills.Count; index++)
        {
            var skill = resolvedSkills[index];
            var inlineSkill = new AgentInlineSkill(skill.Name,
                skill.Description,
                skill.Body,
                license: skill.License,
                compatibility: skill.Compatibility,
                allowedTools: skill.AllowedTools,
                metadata: ToFrontmatterMetadata(skill.Metadata));

            // Registered BEFORE the provider below is constructed: it renders a skill's <available_resources> block from the resources present when it
            // first resolves the skill, so one added afterwards would be readable but never advertised. Mirrors InvocationAgentFactory.BuildInlineSkill.
            if (skill.Resources is { Count: > 0 } resources)
            {
                foreach (var resource in resources)
                {
                    inlineSkill.AddResource(resource.Name, resource.Content, resource.Description);
                }
            }

            inlineSkills[index] = inlineSkill;
        }

#pragma warning disable CA2000 // Ownership transfers to the ChatClientAgent via AIContextProviders; the agent disposes its context providers with itself.
        agentOptions.AIContextProviders =
        [
            new AgentSkillsProvider(inlineSkills,
                new AgentSkillsProviderOptions
                {
                    DisableLoadSkillApproval = true,
                    DisableReadSkillResourceApproval = true
                })
        ];
#pragma warning restore CA2000
#pragma warning restore MAAI001

        // Ids only: the waiver is auditable without a crafted skill name shaping a log line.
        logger.LogInformation(
            "Attached {SkillCount} skill(s) to a spawned sub-agent child with skill-read approval waived ({SkillIds}); the child has no human-in-the-loop approval route and the parent's spawn approval is the consent.",
            resolvedSkills.Count,
            string.Join(", ", resolvedSkills.Select(static skill => skill.Id)));
    }

    // Converts the skill's string metadata map onto the loosely-typed dictionary MAF's frontmatter takes; null for an
    // absent or empty map so a skill without metadata keeps the constructor's default.
    private static AdditionalPropertiesDictionary? ToFrontmatterMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        return metadata is { Count: > 0 }
            ? new AdditionalPropertiesDictionary(metadata.Select(static entry => new KeyValuePair<string, object?>(entry.Key, entry.Value)))
            : null;
    }
}
