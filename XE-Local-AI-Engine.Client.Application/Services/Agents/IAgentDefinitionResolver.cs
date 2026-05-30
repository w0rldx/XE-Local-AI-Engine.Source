namespace XE_Local_AI_Engine.Client.Services.Agents;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Compiles a node-local agent definition into the loopback runtime-package inputs. The resolver projects a bound
///     definition onto the SAME fields the default chat path already feeds into
///     <c>LocalChatRuntimePackageBuilder</c> (system prompt, allowed tools, model profile, reasoning effort, agent
///     version), so the canonical config hash is computed unchanged. A null binding — or one that points at a deleted
///     definition — resolves to <c>null</c>, signalling the caller to keep today's defaults (embedded system prompt,
///     full capability-gated tool offer, agent version 1).
/// </summary>
public interface IAgentDefinitionResolver
{
    /// <summary>
    ///     Resolves the runtime projection for the conversation's bound definition, or <c>null</c> when there is no
    ///     binding (<paramref name="agentDefinitionId" /> is null) or the bound definition no longer exists.
    /// </summary>
    /// <param name="agentDefinitionId">The conversation's bound definition id, or <c>null</c> for the default persona.</param>
    /// <param name="activeModelId">The model the turn runs on; gates the tool offer (capability-aware).</param>
    Task<ResolvedAgentRuntime?> ResolveAsync(Guid? agentDefinitionId, string? activeModelId, CancellationToken cancellationToken = default);
}

/// <summary>
///     The runtime projection of a bound agent definition. Every field maps 1:1 onto a
///     <c>LocalChatRuntimePackageRequest</c> input so the existing builder/config-hash plumbing is reused verbatim.
/// </summary>
public sealed record ResolvedAgentRuntime(
    string ResolvedSystemPrompt,
    IReadOnlyList<AllowedToolDto> AllowedTools,
    string? ModelProfile,
    string? ReasoningEffort,
    int AgentDefinitionVersion);
