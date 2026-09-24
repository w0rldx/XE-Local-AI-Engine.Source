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
            return new ResolvedBinding
            {
                ModelName = request.ModelId,
                Instructions = instructions,
                Tools = null
            };
        }

        var definition = await ResolveDefinitionAsync(request.SubAgentKey!, ct);
        if (definition is null || string.IsNullOrWhiteSpace(definition.ModelProfile))
        {
            return null;
        }

        // Resolve the FULL runtime for the bound child in ONE pass — the same ResolvedAgentRuntime a direct agent send consumes — so it inherits the
        // resolved prompt, reasoning and skills as one unit. Hand over the snapshot already read, not the id: a second read could assemble one child from two versions.
        var resolved = await _agentDefinitionResolver
            .ResolveAsync(definition, definition.ModelProfile, cancellationToken: ct);
        if (resolved is null)
        {
            // The resolver seam is nullable for every caller, so guard it here too: reject with the sanitized
            // unresolved reason rather than degrade to raw instructions (the very bypass this fix closes).
            return null;
        }

        // The profile's OWN curated tool set: offer ∩ AllowedToolNames (already capability-gated by the resolver), bridged to executables, then
        // UNCONDITIONALLY strip spawn_subagent so the child can never spawn — the structural depth cap, whatever its AllowedToolNames lists.
        var tools = CurateChildTools(resolved.AllowedTools);

        // The child model's OWN thinking capability gates the reasoning field, as the direct (resolution.SupportsThinking) and orchestration-participant
        // paths do: a non-thinking Ollama model 400s on think, so ParticipantReasoningOptions omits it. Cache-first. Locality was gated by the resolver above.
        var childCapabilities = await _modelCapabilityResolver
            .ResolveAsync(definition.ModelProfile, ct);
        var (supportsThinking, _, _) = childCapabilities;

        return new ResolvedBinding
        {
            ModelName = definition.ModelProfile,
            Instructions = resolved.ResolvedSystemPrompt,
            Tools = tools,
            // The child model's own reasoning-budget enforceability rides alongside its thinking capability, so a child
            // pinned to a template that renders no reasoning end marker is not handed a cap llama.cpp would ignore.
            Reasoning = new ChildReasoning
            {
                ReasoningEffort = resolved.ReasoningEffort,
                SupportsThinking = supportsThinking,
                ReasoningBudgetEnforceable = childCapabilities.ReasoningBudgetEnforceable
            },
            Skills = resolved.Skills
        };
    }

    // Resolve a persisted definition by GUID id first, then fall back to a case-sensitive name match. A spawn naming an
    // unknown/unbound (no ModelProfile) definition is rejected upstream, never fabricated.
    private async Task<AgentDefinitionRecord?> ResolveDefinitionAsync(string key, CancellationToken ct)
    {
        if (Guid.TryParse(key, out var id))
        {
            return await _definitionStore.GetByIdAsync(id, ct);
        }

        var all = await _definitionStore.ListAsync(ct);
        var match = all.FirstOrDefault(record => string.Equals(record.Name, key, StringComparison.Ordinal));
        if (match is null)
        {
            _logger.LogWarning("Sub-agent spawn referenced an unknown definition key.");
        }

        return match;
    }

    /// <summary>The child's fully-resolved run inputs.</summary>
    /// <remarks>
    ///     <c>Instructions</c> is the resolved system prompt for a profile-bound child — scaffold, persona and injected playbook memory — or
    ///     the raw request instructions for a model-id-only child. <c>Reasoning</c> and <c>Skills</c> are populated only for a profile-bound
    ///     child, staying null for model-id-only so that path is unchanged.
    /// </remarks>
    private sealed record ResolvedBinding
    {
        public required string ModelName { get; init; }

        public required string Instructions { get; init; }

        public required IList<AITool>? Tools { get; init; }

        public ChildReasoning? Reasoning { get; init; }

        public IReadOnlyList<ResolvedSkill>? Skills { get; init; }
    }

    // The child's reasoning inputs: the resolved effort plus the child model's OWN thinking capability, which together
    // drive ParticipantReasoningOptions.Build exactly as the orchestration-participant path does.
    private sealed record ChildReasoning
    {
        public required string? ReasoningEffort { get; init; }

        public required bool SupportsThinking { get; init; }

        public bool ReasoningBudgetEnforceable { get; init; } = true;
    }
}
