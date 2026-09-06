namespace XE_Local_AI_Engine.Client.Services.Capacity;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Instructions;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence;

internal sealed partial class SubAgentSpawnService
{
    private async Task<ResolvedBinding?> ResolveBindingAsync(SubAgentSpawnRequest request, CancellationToken ct)
    {
        // model-id-only binding: no agent profile, so no AllowedToolNames to curate from → the child is tool-less.
        if (!string.IsNullOrWhiteSpace(request.ModelId))
        {
            var instructions = string.IsNullOrWhiteSpace(request.Instructions)
                ? BaseInstructionComposer.Compose(_instructionProvider.GetBaseScaffold(), DefaultSubAgentPersonaInstructions)
                : request.Instructions;
            return new ResolvedBinding(request.ModelId, instructions, Tools: null);
        }

        var definition = await ResolveDefinitionAsync(request.SubAgentKey!, ct).ConfigureAwait(false);
        if (definition is null || string.IsNullOrWhiteSpace(definition.ModelProfile))
        {
            return null;
        }

        // Resolve the FULL runtime for the bound child in ONE pass — the same ResolvedAgentRuntime a direct agent send
        // consumes — so the child inherits the resolved system prompt (scaffold + persona + injected playbook memory),
        // reasoning effort, and skills as one unit, not just its curated tool set. Reading only AllowedTools here used to
        // let a saved sub-agent silently run on raw definition.Instructions with no scaffold, reasoning, or
        // skills — LESS grounding than the anonymous model-id-only path, which already composes the base scaffold.
        // Hand the resolver the snapshot ALREADY read above rather than the id: resolving by id would read the row a
        // second time, and a concurrent edit landing between the two reads would assemble one child out of two
        // versions — its model from this read, its prompt/tools/reasoning/skills from the other.
        var resolved = await _agentDefinitionResolver
                             .ResolveAsync(definition, definition.ModelProfile, cancellationToken: ct)
                             .ConfigureAwait(false);
        if (resolved is null)
        {
            // The resolver seam is nullable for every caller, so guard it here too: reject with the sanitized
            // unresolved reason rather than degrade to raw instructions (the very bypass this fix closes).
            return null;
        }

        // The profile's OWN curated tool set: offer ∩ AllowedToolNames (already capability-gated by the resolver),
        // bridged to executables, then UNCONDITIONALLY strip spawn_subagent so the child can never spawn (the structural
        // depth cap), regardless of what its AllowedToolNames lists.
        var tools = CurateChildTools(resolved.AllowedTools);

        // The child model's OWN thinking capability gates the reasoning field, exactly as the direct path
        // (resolution.SupportsThinking) and the orchestration-participant path (participant.SupportsThinking) gate
        // theirs: a non-thinking Ollama model 400s on think:true/level, so ParticipantReasoningOptions omits the field
        // for it. Cache-first; no probe on a cache hit.
        // The child's knowledge-tool locality gate is applied inside AgentDefinitionResolver above (it classifies the
        // pinned effective model, which for a spawned child IS definition.ModelProfile), so only the thinking bit is
        // taken here; the locality element is ignored.
        var childCapabilities = await _modelCapabilityResolver
                                      .ResolveAsync(definition.ModelProfile, ct)
                                      .ConfigureAwait(false);
        var (supportsThinking, _, _) = childCapabilities;

        return new ResolvedBinding(definition.ModelProfile,
            resolved.ResolvedSystemPrompt,
            tools,
            // The child model's own reasoning-budget enforceability rides alongside its thinking capability, so a child
            // pinned to a template that renders no reasoning end marker is not handed a cap llama.cpp would ignore.
            new ChildReasoning(resolved.ReasoningEffort, supportsThinking, childCapabilities.ReasoningBudgetEnforceable),
            resolved.Skills);
    }

    // Resolve a persisted definition by GUID id first, then fall back to a case-sensitive name match. A spawn naming an
    // unknown/unbound (no ModelProfile) definition is rejected upstream, never fabricated.
    private async Task<AgentDefinitionRecord?> ResolveDefinitionAsync(string key, CancellationToken ct)
    {
        if (Guid.TryParse(key, out var id))
        {
            return await _definitionStore.GetByIdAsync(id, ct).ConfigureAwait(false);
        }

        var all = await _definitionStore.ListAsync(ct).ConfigureAwait(false);
        var match = all.FirstOrDefault(record => string.Equals(record.Name, key, StringComparison.Ordinal));
        if (match is null)
        {
            _logger.LogWarning("Sub-agent spawn referenced an unknown definition key.");
        }

        return match;
    }

    // The child's fully-resolved run inputs. Instructions is the resolved system prompt for a profile-bound child (the
    // scaffold + persona + injected playbook memory), or the raw request instructions for a model-id-only child.
    // Reasoning + Skills are populated only for a profile-bound child (null for model-id-only, keeping that path as-is).
    private sealed record ResolvedBinding(
        string ModelName,
        string Instructions,
        IList<AITool>? Tools,
        ChildReasoning? Reasoning = null,
        IReadOnlyList<ResolvedSkill>? Skills = null);

    // The child's reasoning inputs: the resolved effort plus the child model's OWN thinking capability, which together
    // drive ParticipantReasoningOptions.Build exactly as the orchestration-participant path does.
    private sealed record ChildReasoning(string? ReasoningEffort, bool SupportsThinking, bool ReasoningBudgetEnforceable = true);
}
