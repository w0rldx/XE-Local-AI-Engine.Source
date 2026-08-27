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

        // Two unconditional strips, both structural — a curated child tool is never one of these:
        //   (1) DEPTH CAP: spawn_subagent, so a child can never spawn (mirrored by the runtime Depth guard).
        //   (2) NO HITL ROUTE: any ApprovalRequiredAIFunction. A child runs as an agent-as-tool via
        //       AsAIFunction, which invokes with no per-run options and no approval round-trip — an approval-gated tool
        //       would surface a ToolApprovalRequestContent the child can never answer, silently failing every call to it.
        //       The tools are DROPPED (and warned, naming them), never unwrapped to auto-execute — unwrapping would
        //       bypass the approval control the offer/registry/MCP policy asserted.
        var curated = SubAgentSpawnPolicy.RemoveUnsupportedChildTools(offeredExecutables, out var droppedApprovalTools);
        if (droppedApprovalTools.Count > 0)
        {
            _logger.LogWarning("Dropped {DroppedCount} approval-required tool(s) from a sub-agent child ({DroppedTools}); a spawned child has no human-in-the-loop approval route.",
                droppedApprovalTools.Count,
                string.Join(", ", droppedApprovalTools));
        }

        return curated;
    }

    // Builds a MAF AgentSkillsProvider from the resolved node skills and attaches it to the child agent's options,
    // mirroring InvocationAgentFactory.BuildAgent's skills path (frontmatter + body-as-instructions + bundled
    // resources; scripts are never registered). Empty/null is a no-op so a no-skills child stays byte-identical. The
    // child receives the parent's ALREADY-RESOLVED skill set, so an imported skill arrives fenced — the trust decision
    // was taken once at the resolver and is not re-taken, or reversed, here.
    //
    // Skill-tool approval is waived for children, and only for children. MAF gates load_skill, read_skill_resource and
    // run_skill_script behind approval by default, but these tools arrive through AIContextProviders and so bypass
    // CurateChildTools, which strips approval-required tools precisely because a spawned child has no human-in-the-loop
    // route. Left at the default a child would be handed a load_skill it can never get approved: every call fails
    // silently and an assigned skill is simply unreachable. The parent's approval of the spawn is the consent, and the
    // child's skill set is the parent's resolved set — no wider. run_skill_script keeps its gate: inline skills cannot
    // carry scripts (AddScript takes only a delegate), so the tool always fails closed and must never be pre-approved.
    private static void AttachSkillsProvider(ChatClientAgentOptions agentOptions,
        IReadOnlyList<ResolvedSkill>? skills,
        ILogger<SubAgentSpawnService> logger)
    {
        if (skills is not { Count: > 0 } resolvedSkills)
        {
            return;
        }

        // MAAI001: Agent Skills (AgentSkillsProvider/AgentInlineSkill) shipped [Experimental] in Microsoft.Agents.AI
        // in 1.8.0. The scoped MAAI001 suppression remains at the pinned 1.15.0 until explicit graduation evidence is
        // available. Reached only when the child agent has assigned skills, the
        // same scoped suppression InvocationAgentFactory uses.
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

            // Registered BEFORE the provider below is constructed: the provider renders a skill's <available_resources>
            // block from the resources present when it first resolves the skill, so one added afterwards would be
            // readable but never advertised. Mirrors InvocationAgentFactory.BuildInlineSkill.
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
