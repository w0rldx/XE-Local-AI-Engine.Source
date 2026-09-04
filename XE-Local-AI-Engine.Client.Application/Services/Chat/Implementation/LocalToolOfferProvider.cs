namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.Capacity.Tools;
using XE_Local_AI_Engine.Client.Services.Coder.Tools;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Integrations.Tools;
using XE_Local_AI_Engine.Client.Services.Knowledge.Tools;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;

internal sealed class LocalToolOfferProvider : ILocalToolOfferProvider
{
    private const string BuiltinSource = "builtin";
    private const string McpSourcePrefix = "mcp:";
    private const string McpNamePrefix = "mcp__";

    // Source tag the React tool pickers key their danger badge off; must match parseToolCatalogSource in the frontend.
    private const string CustomSource = "custom";

    // The built-in catalog is static for the process lifetime, so precompute its three projections once. The MCP part
    // is dynamic (servers connect/disconnect) and is read live from the registry on each call, then merged in.
    private readonly IReadOnlyList<AllowedToolDto> _builtinAllTools;

    // The whole capable offer with the knowledge-base tools removed. Returned to a cloud model (unless the operator
    // opted in) so node-local document/chunk/query text is never handed to a third-party provider through a tool call.
    private readonly IReadOnlyList<AllowedToolDto> _builtinAllToolsNoLocalData;
    private readonly bool _allowCloudKnowledgeAccess;
    private readonly IReadOnlyList<LocalToolCatalogEntry> _builtinCatalogEntries;
    private readonly IReadOnlyList<string> _builtinNames;
    private readonly IReadOnlyList<AllowedToolDto> _builtinWithoutAgentHome;
    private readonly IMcpToolRegistry _mcpToolRegistry;

    // Read LIVE per offer, not captured at construction. See IsToolCapable for why.
    private readonly INodeRuntimeSettings _runtimeSettings;

    // Answers the one question the threaded isCloudModel flag cannot: whether the ACTIVE model id — which may be an
    // agent-pinned model different from the turn's — is an external endpoint outside the trust boundary. Consulted
    // synchronously, from the registry's cached generation, exactly like the Codex-catalog check beside it.
    private readonly IModelTrustResolver _modelTrustResolver;

    // This provider is a SINGLETON but the custom-tool catalog is SCOPED (DbContext-backed), so it is resolved from a
    // fresh scope per offer call rather than captured — the established singleton→scoped-store pattern in this codebase.
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AllowedToolDto _spawnOfferDto;
    private readonly AllowedToolDto _computeOfferDto;
    private readonly AllowedToolDto _emitOutputOfferDto;

    // The four work-session state tools, held out of the whole offer for the same reason spawn_subagent is: they are
    // profile-opt-in only. They are also inert outside a session — each handler resolves its session from the ambient
    // conversation id and fails closed when there is none — so the projection here is convenience, not the boundary.
    private readonly IReadOnlyList<AllowedToolDto> _workSessionOfferDtos;

    public LocalToolOfferProvider(IAgentToolRegistry toolRegistry,
        IMcpToolRegistry mcpToolRegistry,
        INodeRuntimeSettings runtimeSettings,
        IServiceScopeFactory scopeFactory,
        IModelTrustResolver modelTrustResolver,
        bool allowCloudKnowledgeAccess)
    {
        ArgumentNullException.ThrowIfNull(toolRegistry);
        _mcpToolRegistry = mcpToolRegistry ?? throw new ArgumentNullException(nameof(mcpToolRegistry));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _modelTrustResolver = modelTrustResolver ?? throw new ArgumentNullException(nameof(modelTrustResolver));
        _allowCloudKnowledgeAccess = allowCloudKnowledgeAccess;

        var builtinDescriptors = toolRegistry.GetLocalChatToolDescriptors();

        // The read-only coder tools (list_files / read_file / search_text) are worker-owned IClientLocalToolHandlers,
        // which IAgentToolRegistry.GetLocalChatToolDescriptors() does NOT project — registering the handlers in DI
        // surfaces them only in the RESOLUTION seam, never the OFFER seam. The agent-send path intersects
        // offered ∩ AllowedToolNames, so without merging them here the seeded Coder agent's tool set would be ∅ and the
        // feature inert. They join the capability-gated (capable-only) built-in set just like run_in_agent_home: present
        // in the full offer, withheld from a non-tool-capable model. The descriptor set is static, so the merged offer
        // stays byte-identical across sends (stable config hash).
        var coderDescriptors = CoderToolDefinition.Descriptors;

        // The read-only knowledge-base tools (search_knowledge_base / read_document / read_surrounding_chunks) are also
        // worker-owned IClientLocalToolHandlers, merged the same way as the coder tools so they appear in the OFFER seam.
        var knowledgeDescriptors = KnowledgeToolCatalog.Descriptors;

        // ask_user is a worker-owned IClientLocalToolHandler too, so it needs the same OFFER-seam merge (the handler
        // registration surfaces it only in the RESOLUTION seam). It is capability-gated exactly like the coder/knowledge
        // tools (it is in capableOnlyNames below) — a model that cannot call tools cannot call this one either, and an
        // offered schema is not free: llama.cpp compiles the whole offered tools array into ONE GBNF grammar with a hard
        // repetition ceiling, and this is the most deeply nested schema in the catalog. It still differs from the
        // coder/knowledge tools in three ways, each deliberate:
        //   * NOT locality-gated (it is absent from localDataToolNames below), so a tool-capable CLOUD model is still
        //     offered it. It sends the model's own question to the local UI — no node-local document, file or workspace
        //     content leaves the node through it — so the cloud-egress gate has nothing to withhold.
        //   * RequiresApproval is STRUCTURAL, not a risk judgement: it is what routes the call through the runner's
        //     out-of-stream approval round-trip, where the human wait happens outside the stream-idle watchdog. A tool
        //     handler cannot simply block for a human — it would trip StreamIdleTimeout. It also means the three
        //     unattended paths (sub-agent, scheduler, delegate-scope inbound MCP), which strip every approval-required
        //     tool, strip this one for free — correct, since none of them has a person to answer. The integration
        //     coordinator is NOT one of them: it does not strip, it composes an explicit offer that this builtin
        //     catalog never joins, so ask_user cannot reach an integration run in the first place.
        //     Agentic-scope inbound MCP is the deliberate exception: it may invoke approval-required tools through the
        //     strict audited auto-approval boundary. If ask_user is selected there, its handler's no-answer fail-safe
        //     returns immediately rather than creating a human wait that an unattended caller cannot satisfy.
        //   * ToolCategory.ReadLocal: it reads an answer from the node-local operator and has no side effect of its own.
        var askUserDescriptor = new LocalChatToolDescriptor(AskUserTool.ToolName,
            AskUserTool.Description,
            AskUserTool.ParameterSchema,
            RequiresApproval: true,
            ToolCategory.ReadLocal);

        // Each tool's Id is derived deterministically from its name so the offer list is byte-identical across sends
        // (the config hash ignores the Id, but a stable Id keeps client-side rendering and equality predictable).
        // The coder and knowledge-base tools are worker-owned IClientLocalToolHandlers merged here so they appear in the
        // OFFER seam (the handler registration surfaces them only in the RESOLUTION seam). They join the
        // capability-gated set just like run_in_agent_home — present in the capable offer, withheld from a non-capable
        // model. spawn_subagent is deliberately NOT folded into this whole offer: it is profile-opt-in only (below).
        _builtinAllTools =
        [
            .. builtinDescriptors.Select(static descriptor => ToOfferDto(descriptor.Name, descriptor.ParameterSchema, descriptor.RequiresApproval, descriptor.Category)),
            .. coderDescriptors.Select(static descriptor => ToOfferDto(descriptor.Name, descriptor.ParameterSchema, descriptor.RequiresApproval, descriptor.Category)),
            .. knowledgeDescriptors.Select(static descriptor => ToOfferDto(descriptor.Name, descriptor.ParameterSchema, descriptor.RequiresApproval, descriptor.Category)),
            ToOfferDto(askUserDescriptor.Name, askUserDescriptor.ParameterSchema, askUserDescriptor.RequiresApproval, askUserDescriptor.Category)
        ];

        // The provider-locality-gated variant of the whole capable offer: BOTH the knowledge-base read tools AND the
        // coder workspace file tools (list_files / read_file / search_text) removed. Offered to a cloud-hosted model
        // (Codex / Azure Foundry) unless the operator opted in via AllowCloudModelAccess, so neither retrieved
        // knowledge-base content NOR node-local workspace/attachment file content reaches a third-party provider through
        // a tool result. Precomputed so the gated offer is byte-identical across sends (stable config hash).
        var localDataToolNames = knowledgeDescriptors.Select(static descriptor => descriptor.Name)
                                                     .Concat(coderDescriptors.Select(static descriptor => descriptor.Name))
                                                     .ToHashSet(StringComparer.Ordinal);
        _builtinAllToolsNoLocalData =
        [
            .. _builtinAllTools.Where(tool => !localDataToolNames.Contains(tool.Name))
        ];

        // spawn_subagent is offered ONLY to an explicit agent profile that opts in via AllowedToolNames — never to the
        // default/mode-off chat path. It is therefore held OUT of the whole offer (_builtinAllTools) and added back only
        // by GetOfferedToolsForProfile (still capability-gated). This is the profile-opt-in seam; loading another model
        // from an unattended plain chat turn is exactly what we are preventing.
        _spawnOfferDto = ToOfferDto(SpawnSubAgentToolDefinition.ToolName, SpawnSubAgentToolDefinition.ParameterSchema, requiresApproval: false, ToolCategory.Orchestration);

        // run_python gets the SAME profile-opt-in treatment as spawn_subagent, and for a sharper reason: it executes
        // model-authored code on the node. It is therefore held out of the whole offer entirely — a default/mode-off
        // chat turn is never offered a code-execution tool — and added back by GetOfferedToolsForProfile only when an
        // agent profile named it in AllowedToolNames. WriteExecute + RequiresApproval is what the UI badges and the
        // approval round-trip key off; the unattended paths (sub-agent, scheduler, delegate-scope inbound MCP) strip
        // every approval-required tool, so they strip this one for free.
        _computeOfferDto = ToOfferDto(ComputeToolDefinition.ToolName, ComputeToolDefinition.ParameterSchema, requiresApproval: true, ToolCategory.WriteExecute);

        // emit_output is held out of EVERY projection — the whole offer, the profile pool, and both known-tool catalogs
        // — so it never reaches chat, the scheduler, a benchmark, MCP, a sub-agent, or the agent-editor tool picker. An
        // integration execution is the only context in which delivering a payload to an external caller means anything,
        // and the integration coordinator is the only caller that may union it in, through the accessor below. That is
        // the same seam ask_user uses, for the same reason: it is a property of the RUN, not a per-agent permission.
        //
        // The approval flag here is the raw declared one; ToOfferDto consults no policy. The coordinator recomposes it
        // through IToolApprovalPolicy at union time, which is the only place it can be composed for this tool.
        _emitOutputOfferDto = ToOfferDto(EmitOutputToolDefinition.ToolName,
            EmitOutputToolDefinition.ParameterSchema,
            requiresApproval: false,
            ToolCategory.ReadLocal);

        // WriteExecute is the honest category: every one of these writes durable session rows, and it is the only write
        // category the enum has. Labelling them ReadLocal to keep them out of a category-based operator policy would
        // hide the write from the layer whose job is to see it — at the cost that tightening WriteExecute in
        // NodeToolApprovalPolicy makes every recorded finding need a click.
        _workSessionOfferDtos =
            [.. WorkSessionToolCatalog.Descriptors.Select(static descriptor => ToOfferDto(descriptor.Name, descriptor.ParameterSchema, descriptor.RequiresApproval, descriptor.Category))];

        // Precompute the capability-gated variant once: the built-ins minus run_in_agent_home, the coder/knowledge tools
        // and ask_user, returned when the active model is not tool-capable. Those tools are offered only to a
        // tool-capable model. The encrypted path stays server-gated and never reaches this provider. (spawn_subagent is
        // not in _builtinAllTools at all, so it never appears in either capability variant of the whole offer.)
        var capableOnlyNames = coderDescriptors.Select(static descriptor => descriptor.Name)
                                               .Concat(knowledgeDescriptors.Select(static descriptor => descriptor.Name))
                                               .Append(askUserDescriptor.Name)
                                               .ToHashSet(StringComparer.Ordinal);
        _builtinWithoutAgentHome =
        [
            .. _builtinAllTools.Where(tool => !string.Equals(tool.Name, AgentHomeToolDefinition.ToolName, StringComparison.Ordinal)
                                              && !capableOnlyNames.Contains(tool.Name))
        ];

        _builtinCatalogEntries =
        [
            .. builtinDescriptors.Select(static descriptor => new LocalToolCatalogEntry
            {
                Name = descriptor.Name,
                Description = descriptor.Description,
                RequiresApproval = descriptor.RequiresApproval,
                Source = BuiltinSource,
                Category = descriptor.Category
            }),
            .. coderDescriptors.Select(static descriptor => new LocalToolCatalogEntry
            {
                Name = descriptor.Name,
                Description = descriptor.Description,
                RequiresApproval = descriptor.RequiresApproval,
                Source = BuiltinSource,
                Category = descriptor.Category
            }),
            .. knowledgeDescriptors.Select(static descriptor => new LocalToolCatalogEntry
            {
                Name = descriptor.Name,
                Description = descriptor.Description,
                RequiresApproval = descriptor.RequiresApproval,
                Source = BuiltinSource,
                Category = descriptor.Category
            }),
            new LocalToolCatalogEntry
            {
                Name = askUserDescriptor.Name,
                Description = askUserDescriptor.Description,
                RequiresApproval = askUserDescriptor.RequiresApproval,
                Source = BuiltinSource,
                Category = askUserDescriptor.Category
            },
            new LocalToolCatalogEntry
            {
                Name = SpawnSubAgentToolDefinition.ToolName,
                Description = SpawnSubAgentToolDefinition.Description,
                RequiresApproval = false,
                Source = BuiltinSource,
                // spawn_subagent drives other agents/models — matches the Orchestration category used for its offer DTO.
                Category = ToolCategory.Orchestration
            },
            new LocalToolCatalogEntry
            {
                Name = ComputeToolDefinition.ToolName,
                Description = ComputeToolDefinition.Description,
                RequiresApproval = true,
                Source = BuiltinSource,
                // run_python runs commands on the node — the existing category for that class, which is what drives the
                // picker's danger badge. Listing it here (ungated by model, like spawn_subagent) is what lets an
                // operator add it to a profile's AllowedToolNames at all; without it CRUD validation would warn on an
                // unknown name and the picker would not show it.
                Category = ToolCategory.WriteExecute
            },
            .. WorkSessionToolCatalog.Descriptors.Select(static descriptor => new LocalToolCatalogEntry
            {
                Name = descriptor.Name,
                Description = descriptor.Description,
                RequiresApproval = descriptor.RequiresApproval,
                Source = BuiltinSource,
                Category = descriptor.Category
            })
        ];

        _builtinNames =
        [
            .. builtinDescriptors.Select(static descriptor => descriptor.Name),
            .. coderDescriptors.Select(static descriptor => descriptor.Name),
            .. knowledgeDescriptors.Select(static descriptor => descriptor.Name),
            askUserDescriptor.Name,
            SpawnSubAgentToolDefinition.ToolName,
            ComputeToolDefinition.ToolName,
            .. WorkSessionToolCatalog.Descriptors.Select(static descriptor => descriptor.Name)
        ];
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         This used to be a <c>HashSet</c> captured at DI composition, with the rationale "singleton + synchronous
    ///         hot path, so a runtime edit applies on the next process restart". That produced a P1: an operator added
    ///         their model in Node Settings, saved successfully, and tool calling still silently returned nothing —
    ///         with no restart hint on the field, unlike four of its neighbours.
    ///     </para>
    ///     <para>
    ///         The seeded design was also internally inconsistent, which is what made it user-visible rather than merely
    ///         stale: two OTHER consumers of this same setting already re-read it live per request —
    ///         <c>GetToolCapableModelsEndpoint</c> (which is what the Agents page displays) and
    ///         <c>OrchestrationResolver.BuildToolCapableSetAsync</c>. So the UI showed the model as tool-capable while
    ///         this seam denied it. Re-reading here removes the outlier rather than introducing a new pattern.
    ///     </para>
    ///     <para>
    ///         The cost is not a file read. <c>INodeRuntimeSettings</c> resolves through <c>CachedNodeSettingsStore</c>,
    ///         whose synchronous <c>Load</c> is an <c>IMemoryCache.TryGetValue</c> hit, and whose <c>SaveAsync</c>
    ///         invalidates AND re-primes the entry — so the read after an operator edit is already warm. This runs once
    ///         per turn (or once per orchestration participant), not per token, and the method already does a live
    ///         per-call read of the MCP registry a few lines below.
    ///     </para>
    /// </remarks>
    public bool IsToolCapable(string? activeModelId)
    {
        if (activeModelId is null)
        {
            return false;
        }

        // Ordinal, case-SENSITIVE, matching the allow-list's documented exact-match contract (a model differing only by
        // case is not capable) and the identical HashSet construction in OrchestrationResolver.BuildToolCapableSetAsync.
        var toolCapableModels = _runtimeSettings.GetToolCapableModels();
        for (var index = 0; index < toolCapableModels.Count; index++)
        {
            if (string.Equals(toolCapableModels[index], activeModelId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<AllowedToolDto> GetOfferedTools(string? activeModelId, bool isCloudModel = false)
    {
        // High-risk tools (run_in_agent_home and every MCP tool) are offered only to a tool-capable model. A
        // null/unknown model id is treated as not capable, so those tools are withheld rather than offered to a model
        // that cannot drive them. The MCP part is read live and sorted so the same catalog state yields a byte-identical
        // offer (stable config hash).
        var capable = IsToolCapable(activeModelId);
        if (!capable)
        {
            // The non-capable variant already excludes the knowledge tools (they are capable-only), so it needs no
            // locality gate.
            return _builtinWithoutAgentHome;
        }

        // Provider-locality gate: withhold the node-local-data tools (knowledge-base read tools AND coder workspace file
        // tools) from a cloud-hosted model unless the operator opted in. The threaded per-turn flag covers the Azure
        // case; the synchronous Codex-catalog and external-trust checks also catch a model pinned to a Codex or ext: id
        // even when the turn's active model was local.
        var gateLocalDataTools = !_allowCloudKnowledgeAccess && IsOutsideTrustBoundary(activeModelId, isCloudModel);
        var baseOffer = gateLocalDataTools ? _builtinAllToolsNoLocalData : _builtinAllTools;

        var mcpDescriptors = _mcpToolRegistry.GetDescriptors();
        if (mcpDescriptors.Count == 0)
        {
            return baseOffer;
        }

        return
        [
            .. baseOffer,
            .. mcpDescriptors.Select(static descriptor => ToOfferDto(descriptor.Name, descriptor.ParameterSchema, descriptor.RequiresApproval, descriptor.Category))
        ];
    }

    public async Task<IReadOnlyList<AllowedToolDto>> GetOfferedToolsAsync(string? activeModelId, bool isCloudModel, CancellationToken cancellationToken = default)
    {
        var baseOffer = GetOfferedTools(activeModelId, isCloudModel);

        // Custom tools are merged ONLY in the tool-capable branch (mirroring the MCP/knowledge gating) and ONLY for a
        // node-local model — a custom command/fetch tool can reach local data and the host, so it is never offered to a
        // cloud model (Azure Foundry, a Codex-pinned id, or a declared-cloud external endpoint), independent of the
        // knowledge-tool cloud opt-in. When the model is non-capable or cloud, the base offer is returned unchanged
        // (byte-identical config hash).
        if (!IsToolCapable(activeModelId) || IsOutsideTrustBoundary(activeModelId, isCloudModel))
        {
            return baseOffer;
        }

        // The node kill-switch is off by default. It is checked here, before the scope and store read, so the common
        // disabled path stays cheap.
        if (!await _runtimeSettings.GetCustomToolsEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            return baseOffer;
        }

        var customDescriptors = await GetEnabledCustomDescriptorsAsync(cancellationToken).ConfigureAwait(false);
        if (customDescriptors.Count == 0)
        {
            return baseOffer;
        }

        return
        [
            .. baseOffer,
            .. customDescriptors.Select(static descriptor => ToOfferDto(descriptor.Name, descriptor.ParameterSchema, descriptor.RequiresApproval, descriptor.Category))
        ];
    }

    /// <inheritdoc />
    public IReadOnlyList<AllowedToolDto> GetIntegrationOutputOffer() => [_emitOutputOfferDto];

    public IReadOnlyList<AllowedToolDto> GetOfferedToolsForProfile(string? activeModelId, bool isCloudModel = false)
    {
        // The profile-intersection pool: the whole offer PLUS spawn_subagent (so a profile that opts in via
        // AllowedToolNames resolves it), still capability-gated. A non-tool-capable model gets the whole offer's
        // non-capable variant and NO spawn tool, so the opt-in cannot bypass the capability gate.
        var capable = IsToolCapable(activeModelId);
        if (!capable)
        {
            return _builtinWithoutAgentHome;
        }

        return [.. GetOfferedTools(activeModelId, isCloudModel), .. SpawnOffer(activeModelId, isCloudModel), .. ComputeOffer(activeModelId, isCloudModel), .. _workSessionOfferDtos];
    }

    public async Task<IReadOnlyList<AllowedToolDto>> GetOfferedToolsForProfileAsync(string? activeModelId, bool isCloudModel, CancellationToken cancellationToken = default)
    {
        // Non-capable models get the non-capable variant and NO spawn/custom tools, so the opt-in cannot bypass the gate.
        if (!IsToolCapable(activeModelId))
        {
            return _builtinWithoutAgentHome;
        }

        // The async whole offer (built-in + MCP + capability/local-gated custom) PLUS the opt-in-only spawn tool — the same
        // asymmetry as the synchronous GetOfferedToolsForProfile, with custom tools folded in through GetOfferedToolsAsync.
        return
        [
            .. await GetOfferedToolsAsync(activeModelId, isCloudModel, cancellationToken).ConfigureAwait(false),
            .. SpawnOffer(activeModelId, isCloudModel),
            .. ComputeOffer(activeModelId, isCloudModel),
            .. _workSessionOfferDtos
        ];
    }

    /// <summary>
    ///     <c>spawn_subagent</c> for the profile pool, or nothing for a model outside the trust boundary.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The same gate as <c>run_python</c>, and for a reason the direct gates alone do not cover. Spawning is
    ///         DELEGATION: the child resolves its own model and its own tool set, so a parent that may not be offered
    ///         the workspace, knowledge-base and custom tools directly could otherwise bind a child to a node-local
    ///         model, have IT read that data, and receive the result back into its own transcript. Every withheld tool
    ///         is reachable that way, which makes an ungated spawn offer a bypass of all three direct gates rather than
    ///         a capability of its own.
    ///     </para>
    ///     <para>
    ///         Withheld unconditionally rather than behind <c>AllowCloudModelAccess</c>: that opt-in governs reading
    ///         node-local data with the operator's knowledge, and it cannot be given informedly about data a child
    ///         agent decides to fetch on its own initiative.
    ///     </para>
    /// </remarks>
    private IReadOnlyList<AllowedToolDto> SpawnOffer(string? activeModelId, bool isCloudModel)
    {
        return IsOutsideTrustBoundary(activeModelId, isCloudModel) ? [] : [_spawnOfferDto];
    }

    /// <summary>
    ///     <c>run_python</c> for the profile pool, or nothing for a cloud-hosted model.
    ///     <para>
    ///         The locality gate here is NOT the knowledge/coder tools' content-leak rationale. What is withheld is the
    ///         ability of a REMOTE model to direct code execution on the operator's machine — the same concern that put
    ///         bare interpreters on <c>HostExecutableGuard</c>'s denylist — so it is withheld unconditionally rather than
    ///         behind the <c>AllowCloudModelAccess</c> opt-in that governs reading node-local data. The synchronous
    ///         Codex and external-trust checks mirror the custom-tool gate: they catch a model pinned to a Codex or
    ///         <c>ext:</c> id even when the turn's active model was local.
    ///     </para>
    /// </summary>
    private IReadOnlyList<AllowedToolDto> ComputeOffer(string? activeModelId, bool isCloudModel)
    {
        return IsOutsideTrustBoundary(activeModelId, isCloudModel) ? [] : [_computeOfferDto];
    }

    /// <summary>
    ///     Whether prompts for <paramref name="activeModelId" /> leave the node, for the three tool gates above.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The formula is: the turn's own cloud flag, OR a Codex-pinned id, OR an external id whose declared
    ///         locality is anything other than Local. That last clause is deliberately not "is declared Cloud": an
    ///         <c>ext:</c> id whose registration cannot be read — a deleted connection, an unreadable store, or the
    ///         window before the startup pass has primed the registry — resolves UNRESOLVED, and only a positively
    ///         resolved local declaration may earn local privileges.
    ///     </para>
    ///     <para>
    ///         Synchronous by necessity: this runs inside the offer seam, which has no async boundary, and blocking a
    ///         send on a file read is not an option. It answers from the registry's cached generation, whose only
    ///         unprimed window is before the node has finished booting.
    ///     </para>
    /// </remarks>
    private bool IsOutsideTrustBoundary(string? activeModelId, bool isCloudModel)
    {
        if (isCloudModel || CodexModelCatalog.IsCodexModel(activeModelId))
        {
            return true;
        }

        return _modelTrustResolver.ClassifyExternalCached(activeModelId) is { } trust && trust != ModelTrustLocality.Local;
    }

    public IReadOnlyList<string> GetKnownToolNames()
    {
        // The full catalog name set: every built-in (capable variant, so the capability-gated tools are still known)
        // plus every live MCP tool. CRUD validation uses this to warn (not fail) on an unknown name.
        var mcpDescriptors = _mcpToolRegistry.GetDescriptors();
        if (mcpDescriptors.Count == 0)
        {
            return _builtinNames;
        }

        return
        [
            .. _builtinNames,
            .. mcpDescriptors.Select(static descriptor => descriptor.Name)
        ];
    }

    public IReadOnlyList<LocalToolCatalogEntry> GetKnownTools()
    {
        // The full catalog as rich entries, UNGATED by model (the agent form shows all tools regardless of the active
        // model). Built-ins are precomputed; MCP entries are read live and tagged with their originating server slug.
        var mcpDescriptors = _mcpToolRegistry.GetDescriptors();
        if (mcpDescriptors.Count == 0)
        {
            return _builtinCatalogEntries;
        }

        return
        [
            .. _builtinCatalogEntries,
            .. mcpDescriptors.Select(static descriptor => new LocalToolCatalogEntry
            {
                Name = descriptor.Name,
                Description = descriptor.Description,
                RequiresApproval = descriptor.RequiresApproval,
                Source = ToMcpSource(descriptor.Name),
                Category = descriptor.Category
            })
        ];
    }

    public async Task<IReadOnlyList<string>> GetKnownToolNamesAsync(CancellationToken cancellationToken = default)
    {
        // UNGATED by capability AND by the node kill-switch: an authored custom tool exists on the node regardless of the
        // active model or the kill-switch, so CRUD collision validation and the agent form see the full name space.
        var customDescriptors = await GetEnabledCustomDescriptorsAsync(cancellationToken).ConfigureAwait(false);
        if (customDescriptors.Count == 0)
        {
            return GetKnownToolNames();
        }

        return
        [
            .. GetKnownToolNames(),
            .. customDescriptors.Select(static descriptor => descriptor.Name)
        ];
    }

    public async Task<IReadOnlyList<LocalToolCatalogEntry>> GetKnownToolsAsync(CancellationToken cancellationToken = default)
    {
        var customDescriptors = await GetEnabledCustomDescriptorsAsync(cancellationToken).ConfigureAwait(false);
        if (customDescriptors.Count == 0)
        {
            return GetKnownTools();
        }

        return
        [
            .. GetKnownTools(),
            .. customDescriptors.Select(static descriptor => new LocalToolCatalogEntry
            {
                Name = descriptor.Name,
                Description = descriptor.Description,
                RequiresApproval = descriptor.RequiresApproval,
                Source = CustomSource,
                Category = descriptor.Category,
                IsFixedCustomTool = descriptor.IsFixedCustomTool
            })
        ];
    }

    // Reads the enabled, acknowledged custom-tool offer descriptors LIVE from the scoped catalog through a fresh scope
    // (this provider is a singleton). The catalog leaves the node kill-switch OUT of GetDescriptorsAsync so the ungated
    // known-tools views see authored tools even when the feature is switched off; the OFFER methods apply the kill-switch
    // themselves before calling this. Reading no cache mirrors how the MCP registry is read live per offer.
    private async Task<IReadOnlyList<LocalChatToolDescriptor>> GetEnabledCustomDescriptorsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ICustomToolCatalog>();
        return await catalog.GetDescriptorsAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AllowedToolDto ToOfferDto(string name, string? parameterSchema, bool requiresApproval, ToolCategory category)
    {
        return new AllowedToolDto
        {
            Id = DeriveDeterministicId(name),
            Name = name,
            Location = ToolLocation.ClientLocal,
            ParameterSchema = parameterSchema,
            RequiresApproval = requiresApproval,
            Category = category
        };
    }

    /// <summary>
    ///     Derives the catalog source tag for an MCP tool from its qualified name <c>mcp__{slug}__{tool}</c>, yielding
    ///     <c>mcp:{slug}</c> so the UI can group tools by their originating server. A name that does not match the
    ///     expected shape falls back to the bare <c>mcp</c> tag.
    /// </summary>
    private static string ToMcpSource(string qualifiedName)
    {
        if (qualifiedName.StartsWith(McpNamePrefix, StringComparison.Ordinal))
        {
            var rest = qualifiedName[McpNamePrefix.Length..];
            var separatorIndex = rest.IndexOf("__", StringComparison.Ordinal);
            if (separatorIndex > 0)
            {
                return McpSourcePrefix + rest[..separatorIndex];
            }
        }

        return "mcp";
    }

    private static Guid DeriveDeterministicId(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"local-tool:{name}"));
        return new Guid(hash.AsSpan(start: 0, length: 16));
    }
}
