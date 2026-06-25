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
    /// </summary>
    IReadOnlyList<AllowedToolDto> GetOfferedTools(string? activeModelId);

    /// <summary>
    ///     The offer pool an EXPLICIT agent profile may intersect against (<c>offered ∩ AllowedToolNames</c>). Identical
    ///     to <see cref="GetOfferedTools" /> EXCEPT it also includes <c>spawn_subagent</c> (still capability-gated), so a
    ///     profile that lists <c>spawn_subagent</c> in its <c>AllowedToolNames</c> on a tool-capable model resolves it,
    ///     while the default/mode-off path (which uses <see cref="GetOfferedTools" />) never does. This is the
    ///     opt-in-only seam for the spawn tool.
    /// </summary>
    IReadOnlyList<AllowedToolDto> GetOfferedToolsForProfile(string? activeModelId);

    /// <summary>
    ///     The names of every catalog tool, independent of model capability gating. This is the canonical set of tool
    ///     names that exist on the node; the agent-definition CRUD validation uses it to warn (not fail) when a
    ///     definition references a name that is not in the catalog, and the agent-management UI reuses it as the tool
    ///     picker's source. Capability gating (which of these a given model is actually offered) stays in
    ///     <see cref="GetOfferedTools" />.
    /// </summary>
    IReadOnlyList<string> GetKnownToolNames();

    /// <summary>
    ///     The full tool catalog as rich entries (name + description + approval flag + source), independent of model
    ///     capability gating. This is the single source the tool-catalog endpoint and the React tool pickers consume:
    ///     it lists every built-in tool plus every tool discovered from an enabled MCP server, so the agent form can
    ///     show all tools regardless of the active model. <see cref="LocalToolCatalogEntry.Source" /> is
    ///     <c>"builtin"</c> or <c>"mcp:{serverSlug}"</c>. Capability gating stays in <see cref="GetOfferedTools" />;
    ///     <see cref="GetKnownToolNames" /> remains the names-only view used by CRUD validation.
    /// </summary>
    IReadOnlyList<LocalToolCatalogEntry> GetKnownTools();
}
