namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     Surfaces the local tool catalog as the transport-level offer list.
/// </summary>
/// <remarks>
///     The Client-layer abstraction over the internal <c>IAgentToolRegistry</c>, projecting its descriptors into
///     <c>ClientLocal</c> <see cref="AllowedToolDto" />s; what each projection carries is in <c>docs/wiki/05-chat.md</c>.
///     A caller holding a <see cref="ModelCapabilitySnapshot" /> passes its <c>CloudGrants</c> as <c>cloudGrants</c>,
///     so the turn reads grants once; null falls back to the trust resolver's cached answer. Grants never change
///     whether the model leaves the node.
/// </remarks>
public interface ILocalToolOfferProvider
{
    /// <summary>
    ///     The catalog tools as offer-list DTOs for the given active model: the WHOLE offer the default chat path and
    ///     the seeded "Default Assistant" receive verbatim.
    /// </summary>
    /// <remarks>
    ///     The executables are resolved by the invocation factory from the registry by name; this list travels in the
    ///     runtime package only for the config hash and client display. Capability-gated tools are omitted when
    ///     <paramref name="activeModelId" /> is not in the tool-capable allow-list, and <c>spawn_subagent</c> is absent
    ///     entirely (see <see cref="GetOfferedToolsForProfile" />). The knowledge-base read tools are withheld from a
    ///     cloud model unless the <c>AllowCloudModelAccess</c> node setting is set.
    /// </remarks>
    /// <param name="isCloudModel">
    ///     The per-turn locality the caller already resolved; this seam performs no lookup of its own.
    /// </param>
    /// <param name="cloudGrants">The snapshot's grants for the model; null reads the trust resolver's cached answer once.</param>
    IReadOnlyList<AllowedToolDto> GetOfferedTools(string? activeModelId, bool isCloudModel = false, ExternalProviderCloudGrants? cloudGrants = null);

    /// <summary>
    ///     Whether the operator-maintained tool-capable allow-list (<c>AgentHome:ToolCapableModels</c>, read LIVE per
    ///     call) admits <paramref name="activeModelId" />.
    /// </summary>
    /// <remarks>
    ///     A null or unknown id is never capable and the match is <see cref="StringComparison.Ordinal" />. This is the
    ///     predicate every offer method applies FIRST, and it states operator PERMISSION, a source free to disagree
    ///     with the template-detected capability an <c>IModelCapabilityResolver</c> reports. It is exposed so a caller
    ///     that must REFUSE up front can ask the same question instead of re-implementing the allow-list read.
    /// </remarks>
    bool IsToolCapable(string? activeModelId);

    /// <summary>
    ///     The FULL offer: <see cref="GetOfferedTools" /> PLUS the node's enabled, acknowledged CUSTOM tools.
    /// </summary>
    /// <remarks>
    ///     Custom tools read from a DbContext-backed store, hence the asynchrony; the synchronous overload stays the
    ///     built-in and MCP core. They merge ONLY in the tool-capable branch, ONLY for a node-LOCAL model — a custom
    ///     command or fetch tool reaches local data and the host — and only while <c>NodeSettings.CustomToolsEnabled</c>
    ///     is on, default off. Otherwise the result is byte-identical to <see cref="GetOfferedTools" />.
    /// </remarks>
    Task<IReadOnlyList<AllowedToolDto>> GetOfferedToolsAsync(string? activeModelId, bool isCloudModel, ExternalProviderCloudGrants? cloudGrants = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     The offer pool an EXPLICIT agent profile may intersect against (<c>offered ∩ AllowedToolNames</c>).
    /// </summary>
    /// <remarks>
    ///     Identical to <see cref="GetOfferedTools" /> except that it also includes the opt-in-only tools, still
    ///     capability-gated, so a profile that lists <c>spawn_subagent</c> on a tool-capable model resolves it while
    ///     the default path never does. The same provider-locality gate applies.
    /// </remarks>
    IReadOnlyList<AllowedToolDto> GetOfferedToolsForProfile(string? activeModelId, bool isCloudModel = false, ExternalProviderCloudGrants? cloudGrants = null);

    /// <summary>
    ///     The profile intersection pool PLUS the node's enabled, acknowledged custom tools, under the same gates as
    ///     <see cref="GetOfferedToolsAsync" />; the synchronous overload never carries custom tools.
    /// </summary>
    Task<IReadOnlyList<AllowedToolDto>> GetOfferedToolsForProfileAsync(string? activeModelId, bool isCloudModel, ExternalProviderCloudGrants? cloudGrants = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     The tools the three cloud-model switches removed from the PROFILE pool for this model, each tagged with its
    ///     switch; empty for a model that stays on the node or is not tool-capable.
    /// </summary>
    /// <remarks>
    ///     Web tools count only while <c>WebAccessEnabled</c> is on (otherwise the node switch, not the cloud one,
    ///     withheld them), and HttpFetch custom tools only while <c>CustomToolsEnabled</c> is on too. Drives the
    ///     <c>CloudToolsWithheld</c> turn notice.
    /// </remarks>
    Task<IReadOnlyList<CloudWithheldTool>> GetCloudWithheldToolsAsync(string? activeModelId, bool isCloudModel, ExternalProviderCloudGrants? cloudGrants = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     The ONE built-in tool an integration execution is additionally offered, and the only way to reach it.
    /// </summary>
    /// <remarks>
    ///     <c>emit_output</c> is held out of every other projection here, so an agent definition cannot grant it:
    ///     delivering a result to the caller is a property of RUNNING an integration execution. <b>The approval flag on
    ///     the returned descriptor is the raw declared one and the CALLER must compose it</b> — this provider consults
    ///     no <c>IToolApprovalPolicy</c>, so the integration coordinator recomposes it through the node policy before
    ///     the agent is built, and a node that tightens <c>ReadLocal</c> tightens this tool with it.
    /// </remarks>
    IReadOnlyList<AllowedToolDto> GetIntegrationOutputOffer();

    /// <summary>
    ///     The canonical set of tool names that exist on the node, independent of model capability gating.
    /// </summary>
    /// <remarks>
    ///     Agent-definition CRUD validation uses it to warn, not fail, on a name that is not in the catalog, and the
    ///     agent-management UI reuses it as the tool picker's source. Capability gating stays in
    ///     <see cref="GetOfferedTools" />.
    /// </remarks>
    IReadOnlyList<string> GetKnownToolNames();

    /// <summary>
    ///     <see cref="GetKnownToolNames" /> PLUS the names of every enabled, acknowledged custom tool, UNGATED by model
    ///     capability and by the node kill-switch, so CRUD collision validation sees the full name space.
    /// </summary>
    Task<IReadOnlyList<string>> GetKnownToolNamesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The full tool catalog as rich entries — name, description, approval flag and source — independent of model
    ///     capability gating.
    /// </summary>
    /// <remarks>
    ///     This is the single source the tool-catalog endpoint and the React pickers consume: every built-in plus every
    ///     tool discovered from an enabled MCP server, so the agent form shows all of them whatever the active model.
    ///     <see cref="LocalToolCatalogEntry.Source" /> is <c>"builtin"</c> or <c>"mcp:{serverSlug}"</c>.
    /// </remarks>
    IReadOnlyList<LocalToolCatalogEntry> GetKnownTools();

    /// <summary>
    ///     <see cref="GetKnownTools" /> PLUS a rich entry for every enabled, acknowledged custom tool, tagged
    ///     <see cref="LocalToolCatalogEntry.Source" /> <c>"custom"</c> so the React pickers render a danger badge.
    /// </summary>
    /// <remarks>
    ///     UNGATED by model capability and by the node kill-switch, mirroring <see cref="GetKnownToolNamesAsync" />.
    /// </remarks>
    Task<IReadOnlyList<LocalToolCatalogEntry>> GetKnownToolsAsync(CancellationToken cancellationToken = default);
}
