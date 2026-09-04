namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Surfaces the local tool catalog as the transport-level offer list. This is the clean Client-layer abstraction
///     over the internal <c>IAgentToolRegistry</c>: it converts the registry's tool descriptors into
///     <see cref="AllowedToolDto" />s (name + schema + approval flag, located <c>ClientLocal</c>) so the send and
///     regenerate paths can attach the offer list without depending on the AI.Agent assembly's internals.
/// </summary>
public interface ILocalToolOfferProvider
{
    /// <summary>
    ///     The catalog tools as offer-list DTOs for the given active model — the WHOLE offer the default/mode-off chat
    ///     path and the seeded "Default Assistant" receive verbatim. The executables themselves are resolved by the
    ///     invocation factory from the registry by name; this list only travels in the runtime package for the config
    ///     hash and client display. High-risk catalog tools that require a tool-capable model (currently
    ///     <c>run_in_agent_home</c>) are omitted when <paramref name="activeModelId" /> is not in the worker's
    ///     tool-capable allow-list, so an incapable loopback model is never offered the tool. <c>spawn_subagent</c> is
    ///     NOT in this whole offer — it is offered ONLY to an explicit agent profile that
    ///     opts in via its <c>AllowedToolNames</c> (see <see cref="GetOfferedToolsForProfile" />), never to a plain chat
    ///     turn. The encrypted path stays server-gated and does not call this seam.
    ///     <para>
    ///         Provider-locality gate: the read-only knowledge-base tools (<c>search_knowledge_base</c>,
    ///         <c>read_document</c>, <c>read_surrounding_chunks</c>) are withheld when
    ///         <paramref name="isCloudModel" /> is <see langword="true" /> (or the model id is a Codex model) unless the
    ///         operator opted in via <c>KnowledgeBase:AllowCloudModelAccess</c>, so node-local document/chunk/query text
    ///         is never handed to a cloud model through a tool call by default. <paramref name="isCloudModel" /> is the
    ///         per-turn locality already resolved by the caller (Codex / Azure Foundry = cloud) — this seam does not
    ///         perform its own lookup.
    ///     </para>
    /// </summary>
    IReadOnlyList<AllowedToolDto> GetOfferedTools(string? activeModelId, bool isCloudModel = false);

    /// <summary>
    ///     Whether the node's operator-maintained tool-capable allow-list (the migrated
    ///     <c>AgentHome:ToolCapableModels</c>, read LIVE per call) admits <paramref name="activeModelId" />. A
    ///     null/unknown model id is never capable, and the match is <see cref="StringComparison.Ordinal" /> — a model
    ///     differing only by case is not admitted.
    ///     <para>
    ///         This is the predicate every offer method above applies FIRST, and it is a statement about operator
    ///         permission, not about the model: it is a different, freely disagreeing source from the template-detected
    ///         capability an <c>IModelCapabilityResolver</c> reports. It is exposed so a caller that must REFUSE up
    ///         front — rather than silently receive a thinner offer — can ask the same question the offer will ask,
    ///         instead of re-implementing the allow-list read. Callers that only need the tools should keep using the
    ///         offer methods.
    ///     </para>
    /// </summary>
    bool IsToolCapable(string? activeModelId);

    /// <summary>
    ///     The FULL offer for the given active model — the synchronous built-in + MCP offer
    ///     (<see cref="GetOfferedTools" />) PLUS the node's enabled, acknowledged user-defined CUSTOM tools. Custom tools
    ///     read from a DbContext-backed store, so this is asynchronous; the synchronous overload stays the built-in + MCP
    ///     core. Custom tools are merged ONLY in the tool-capable branch and ONLY for a node-LOCAL model (a custom
    ///     command/fetch tool can reach local data and the host, so it is never offered to a cloud model) and only when the
    ///     node kill-switch <c>NodeSettings.CustomToolsEnabled</c> is on (default off). When off / cloud / non-capable, the
    ///     result is byte-identical to <see cref="GetOfferedTools" />.
    /// </summary>
    Task<IReadOnlyList<AllowedToolDto>> GetOfferedToolsAsync(string? activeModelId, bool isCloudModel, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The offer pool an EXPLICIT agent profile may intersect against (<c>offered ∩ AllowedToolNames</c>). Identical
    ///     to <see cref="GetOfferedTools" /> EXCEPT it also includes <c>spawn_subagent</c> (still capability-gated), so a
    ///     profile that lists <c>spawn_subagent</c> in its <c>AllowedToolNames</c> on a tool-capable model resolves it,
    ///     while the default/mode-off path (which uses <see cref="GetOfferedTools" />) never does. This is the
    ///     opt-in-only seam for the spawn tool. The same knowledge-tool provider-locality gate as
    ///     <see cref="GetOfferedTools" /> applies.
    /// </summary>
    IReadOnlyList<AllowedToolDto> GetOfferedToolsForProfile(string? activeModelId, bool isCloudModel = false);

    /// <summary>
    ///     The profile intersection pool (<see cref="GetOfferedToolsForProfile" />) PLUS the node's enabled, acknowledged
    ///     custom tools, under the same capability / node-local / kill-switch gate as <see cref="GetOfferedToolsAsync" />.
    ///     A profile that lists a <c>custom__…</c> tool in its <c>AllowedToolNames</c> resolves it only through this async
    ///     pool; the synchronous overload never carries custom tools.
    /// </summary>
    Task<IReadOnlyList<AllowedToolDto>> GetOfferedToolsForProfileAsync(string? activeModelId, bool isCloudModel, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The ONE built-in tool an integration execution is additionally offered, and the only way to reach it.
    ///     <c>emit_output</c> is held out of every other projection here — the whole offer, the profile pool, and both
    ///     known-tool catalogs — so it never reaches chat, the scheduler, a benchmark, MCP, a sub-agent, or the
    ///     agent-editor tool picker, and an agent definition cannot grant it. Delivering a result to the caller is a
    ///     property of RUNNING an integration execution, not a per-agent permission, which is exactly the seam
    ///     <c>ask_user</c> uses.
    ///     <para>
    ///         <b>The approval flag on the returned descriptor is the raw declared one and the CALLER must compose it.</b>
    ///         This provider consults no <c>IToolApprovalPolicy</c> anywhere, and the union is the only place this
    ///         tool's flag can be composed — so the integration coordinator recomposes it through the node policy before
    ///         the agent is constructed. A node that tightens <c>ReadLocal</c> therefore tightens this tool with it, and
    ///         an unattended run fails closed at the first call rather than losing the capability silently.
    ///     </para>
    /// </summary>
    IReadOnlyList<AllowedToolDto> GetIntegrationOutputOffer();

    /// <summary>
    ///     The names of every catalog tool, independent of model capability gating. This is the canonical set of tool
    ///     names that exist on the node; the agent-definition CRUD validation uses it to warn (not fail) when a
    ///     definition references a name that is not in the catalog, and the agent-management UI reuses it as the tool
    ///     picker's source. Capability gating (which of these a given model is actually offered) stays in
    ///     <see cref="GetOfferedTools" />.
    /// </summary>
    IReadOnlyList<string> GetKnownToolNames();

    /// <summary>
    ///     <see cref="GetKnownToolNames" /> PLUS the names of every enabled, acknowledged custom tool. UNGATED by model
    ///     capability AND by the node kill-switch (an authored custom tool exists on the node whether or not the feature is
    ///     currently switched on), so CRUD collision validation and the agent tool picker see the full name space.
    /// </summary>
    Task<IReadOnlyList<string>> GetKnownToolNamesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The full tool catalog as rich entries (name + description + approval flag + source), independent of model
    ///     capability gating. This is the single source the tool-catalog endpoint and the React tool pickers consume:
    ///     it lists every built-in tool plus every tool discovered from an enabled MCP server, so the agent form can
    ///     show all tools regardless of the active model. <see cref="LocalToolCatalogEntry.Source" /> is
    ///     <c>"builtin"</c> or <c>"mcp:{serverSlug}"</c>. Capability gating stays in <see cref="GetOfferedTools" />;
    ///     <see cref="GetKnownToolNames" /> remains the names-only view used by CRUD validation.
    /// </summary>
    IReadOnlyList<LocalToolCatalogEntry> GetKnownTools();

    /// <summary>
    ///     <see cref="GetKnownTools" /> PLUS a rich entry for every enabled, acknowledged custom tool, tagged
    ///     <see cref="LocalToolCatalogEntry.Source" /> <c>"custom"</c> so the React pickers render a danger badge. UNGATED
    ///     by model capability AND by the node kill-switch, mirroring <see cref="GetKnownToolNamesAsync" />.
    /// </summary>
    Task<IReadOnlyList<LocalToolCatalogEntry>> GetKnownToolsAsync(CancellationToken cancellationToken = default);
}
