namespace XE_Local_AI_Engine.Client.Services.Agents;

using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence;

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
    /// <param name="retrievalQuery">
    ///     The incoming user-turn text used to relevance-gate playbook injection (relevance retrieval and cohort monitoring, the relevance-retrieval gate). When the agent
    ///     has more than the configured threshold of Enabled actions and this is non-blank, only the top-k most relevant
    ///     actions are injected; otherwise (blank query, or at/below the threshold) the full static prepend is used, so the
    ///     resolved prompt — and the config hash — stays byte-identical to the pre-retrieval path.
    /// </param>
    /// <param name="supportsTools">
    ///     Whether the active model advertises the Ollama <c>tools</c> capability. When <c>false</c> ALL tool offers are
    ///     withheld (the model cannot drive tool calls), independent of the existing per-tool <c>ToolCapableModels</c>
    ///     name allow-list. Defaults to <c>true</c> so callers that do not gate by capability keep today's behaviour.
    /// </param>
    /// <param name="honorModelProfile">
    ///     Whether the definition's pinned <c>ModelProfile</c> applies. When <c>true</c> (default) the pin — when set —
    ///     is the model the turn runs on: it gates the tool offer and is returned as the resolved
    ///     <see cref="ResolvedAgentRuntime.ModelProfile" />. When <c>false</c> the caller supplied an explicit concrete
    ///     model (the user picked one in the chat dropdown) that must win over the pin: the pin is suppressed entirely,
    ///     the tool offer is gated by <paramref name="activeModelId" />, and the resolved <c>ModelProfile</c> is
    ///     <c>null</c> so the caller's <c>resolved?.ModelProfile ?? activeModel</c> yields the user's pick. Defaults to
    ///     <c>true</c> so callers that do not override the pin keep today's behaviour.
    /// </param>
    /// <param name="activeModelIsCloud">
    ///     Whether the turn's active model is cloud-hosted (Codex OAuth / Azure Foundry), resolved once per turn by the
    ///     caller. Gates the knowledge-base tools off the offer for a cloud model (unless the operator opted in via
    ///     <c>KnowledgeBase:AllowCloudModelAccess</c>) so node-local document/chunk/query text is not handed to a cloud
    ///     provider through a tool result. Defaults to <c>false</c> (local) so callers that do not classify locality keep
    ///     today's behaviour.
    /// </param>
    Task<ResolvedAgentRuntime?> ResolveAsync(Guid? agentDefinitionId, string? activeModelId, string? retrievalQuery = null, bool supportsTools = true, bool honorModelProfile = true,
        bool activeModelIsCloud = false, CancellationToken cancellationToken = default);
}

/// <summary>
///     The runtime projection of a bound agent definition. The first five fields map 1:1 onto a
///     <c>LocalChatRuntimePackageRequest</c> input so the existing builder/config-hash plumbing is reused verbatim;
///     <see cref="AgentDefinitionId" /> and <see cref="AgentName" /> carry the resolved agent's provenance + display-name
///     snapshot so the stream service stamps per-response attribution without a second fetch. The attribution members are
///     trailing (with defaults) so they never participate in the config hash and never affect positional construction.
///     <see cref="Skills" /> is likewise trailing (defaults to null/empty): the enabled+assigned skill set used for MAF
///     progressive disclosure. It is NOT folded into <see cref="ResolvedSystemPrompt" /> (bodies load on demand), so the
///     runtime-package builder folds it into the config hash separately and threads it to the invocation factory.
///     <see cref="PlaybookEnabled" /> is a trailing attribution snapshot too: it lets the post-run memory-extraction
///     seam gate without re-fetching the definition. Like the other trailing members it does NOT participate in the
///     config hash (the builder reads only the leading five fields) and never affects positional construction.
///     <see cref="MemoryExtractionEnabled" /> is the companion gate: extraction fires only when BOTH it and
///     <see cref="PlaybookEnabled" /> are true; retrieval/injection stays gated on <see cref="PlaybookEnabled" /> alone,
///     so a retrieval-only agent (<see cref="MemoryExtractionEnabled" /> false) still injects existing memory but mines
///     no new candidates. It is trailing/non-config-affecting for the same reason as <see cref="PlaybookEnabled" />.
///     <see cref="Kind" /> is the definition's execution shape, also a trailing/non-config-affecting attribution snapshot:
///     the resolver already loaded and decrypted the definition, so exposing its Kind lets the chat-turn
///     resolver decide whether to compile an orchestration WITHOUT a second uncached store read + AES-GCM decrypt on
///     every send (the common non-orchestrator path skips the reload entirely).
/// </summary>
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
    // Per-agent opt-out from the send-time tool-relevance filter. Trailing and non-config-affecting for the same reason
    // as the members above: the filter narrows only the array handed to the provider, never the offer or the prompt.
    //
    // [JsonIgnore] — UNLIKE every other member here, this one is kept off the wire entirely. This record is serialized
    // verbatim into the FROZEN v1 benchmark runtime snapshot, whose stored bytes are re-hashed to validate
    // `configurationHash`, so a new member emitting `false` would change the bytes of every already-frozen run and
    // every one of them would stop replaying with "configuration hash is invalid"
    // (`BenchmarkRuntimeSnapshotV1CompatibilityTests` is the guard). Omitting it is also the honest shape: a benchmark
    // never honours the opt-out — `BenchmarkRunExecutor.BuildPrimaryPackage` does not thread this flag into the
    // replayed `RuntimePackage`, so every frozen run generates under the node-level filter setting whatever the agent
    // asked for. Nothing else serializes this record; the agent-definition endpoint DTOs carry their own copy of the
    // flag off `AgentDefinitionRecord` and are unaffected.
    [property: JsonIgnore]
    bool DisableToolRelevanceFilter = false);
