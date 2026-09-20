namespace XE_Local_AI_Engine.Client.Services.Agents;

using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Compiles a node-local agent definition into the loopback runtime-package inputs.
/// </summary>
/// <remarks>
///     A bound definition projects onto the SAME fields the default chat path feeds into
///     <c>LocalChatRuntimePackageBuilder</c> — system prompt, allowed tools, model profile, reasoning effort, agent
///     version — so the canonical config hash is computed unchanged. A null binding, or one pointing at a deleted
///     definition, resolves to <c>null</c>: the caller keeps the embedded prompt, full offer and agent version 1.
/// </remarks>
public interface IAgentDefinitionResolver
{
    /// <summary>
    ///     Resolves the runtime projection for the conversation's bound definition, or <c>null</c> when there is no binding or the bound definition no longer exists.
    /// </summary>
    /// <remarks>
    ///     <paramref name="retrievalQuery" /> relevance-gates playbook injection: above the configured action
    ///     threshold and non-blank only the top-k actions are injected, and otherwise the full static prepend keeps
    ///     prompt and hash byte-identical. A false <paramref name="supportsTools" /> withholds ALL offers, whatever
    ///     the <c>ToolCapableModels</c> allow-list says, and a false <paramref name="honorModelProfile" /> suppresses
    ///     the definition's pin so the caller's pick wins and the resolved profile comes back <c>null</c>.
    /// </remarks>
    /// <param name="activeModelId">The model the turn runs on; gates the tool offer, capability-aware.</param>
    /// <param name="retrievalQuery">The user-turn text that relevance-gates playbook injection.</param>
    /// <param name="supportsTools">Whether the active model advertises the <c>tools</c> capability.</param>
    /// <param name="honorModelProfile">Whether the pin reaches <see cref="ResolvedAgentRuntime.ModelProfile" />.</param>
    /// <param name="activeModelIsCloud">Whether the turn's model is cloud-hosted.</param>
    Task<ResolvedAgentRuntime?> ResolveAsync(Guid? agentDefinitionId, string? activeModelId, string? retrievalQuery = null, bool supportsTools = true, bool honorModelProfile = true,
        bool activeModelIsCloud = false, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The same projection as the id overload, over a definition the caller has ALREADY read.
    /// </summary>
    /// <remarks>
    ///     It saves a second store read, and more importantly stops one runtime being assembled out of two reads a
    ///     concurrent edit can make disagree: the snapshot makes the projection provably one version.
    /// </remarks>
    /// <param name="definition">The already-read definition snapshot to project. Never <c>null</c>.</param>
    /// <param name="activeModelId">The model the turn runs on; gates the tool offer, capability-aware.</param>
    /// <param name="retrievalQuery">Relevance-gates playbook injection, as on the id overload.</param>
    /// <param name="supportsTools">Whether the model advertises <c>tools</c>, as on the id overload.</param>
    /// <param name="honorModelProfile">Whether the pinned <c>ModelProfile</c> applies, as on the id overload.</param>
    /// <param name="activeModelIsCloud">Whether the turn's model is cloud-hosted, as on the id overload.</param>
    /// <param name="cancellationToken">Cancels the projection's store reads.</param>
    /// <returns>The projection of <paramref name="definition" />; nullable only to stay substitutable.</returns>
    Task<ResolvedAgentRuntime?> ResolveAsync(AgentDefinitionRecord definition, string? activeModelId, string? retrievalQuery = null, bool supportsTools = true,
        bool honorModelProfile = true, bool activeModelIsCloud = false, CancellationToken cancellationToken = default);
}

/// <summary>
///     The runtime projection of a bound agent definition.
/// </summary>
/// <remarks>
///     The first five fields map one-to-one onto a <c>LocalChatRuntimePackageRequest</c> input, so the existing
///     builder and config-hash plumbing are reused verbatim. EVERY member after them is trailing, defaulted and
///     outside the config hash, which reads only those five, so adding one changes neither an existing hash nor
///     positional construction. What each carries and why:
///     docs/wiki/04-agent-mode.md ("The resolved runtime projection").
/// </remarks>
public sealed record ResolvedAgentRuntime(
    string ResolvedSystemPrompt,
    IReadOnlyList<AllowedToolDto> AllowedTools,
    string? ModelProfile,
    string? ReasoningEffort,
    int AgentDefinitionVersion,
    Guid AgentDefinitionId = default,
    string AgentName = "",
    IReadOnlyList<ResolvedSkill>? Skills = null,
    bool PlaybookEnabled = false,
    bool MemoryExtractionEnabled = true,
    bool EffectiveModelIsCloud = false,
    AgentDefinitionKind Kind = AgentDefinitionKind.Single,
    IReadOnlyList<ResolvedCustomTool>? CustomTools = null,
    // Per-agent opt-out from the send-time tool-relevance filter, and the ONE member kept off the wire: this record
    // is serialized into the frozen v1 benchmark snapshot, whose bytes are re-hashed. See the wiki section above.
    [property: JsonIgnore]
    bool DisableToolRelevanceFilter = false);
