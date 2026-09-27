namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

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
    ///     scripts) and attaches it to the child's options through the same <see cref="InvocationSkillsProvider" /> <c>InvocationAgentFactory</c> uses.
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

#pragma warning disable CA2000 // Ownership transfers to the ChatClientAgent via AIContextProviders; the agent disposes its context providers with itself.
        agentOptions.AIContextProviders = [InvocationSkillsProvider.CreateForSubAgentChild(InvocationRunner.MapSkills(resolvedSkills)!)];
#pragma warning restore CA2000

        // Ids only: the waiver is auditable without a crafted skill name shaping a log line.
        logger.LogInformation(
            "Attached {SkillCount} skill(s) to a spawned sub-agent child with skill-read approval waived ({SkillIds}); the child has no human-in-the-loop approval route and the parent's spawn approval is the consent.",
            resolvedSkills.Count,
            string.Join(", ", resolvedSkills.Select(static skill => skill.Id)));
    }
}
