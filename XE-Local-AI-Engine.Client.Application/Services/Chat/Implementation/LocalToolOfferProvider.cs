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

    // The whole offer with every capability-gated tool removed, returned when the active model is not tool-capable.
    private readonly IReadOnlyList<AllowedToolDto> _builtinAllToolsNonCapable;
    private readonly IMcpToolRegistry _mcpToolRegistry;

    // Read LIVE per offer, not captured at construction. See IsToolCapable for why.
    private readonly INodeRuntimeSettings _runtimeSettings;

    // Answers what the threaded isCloudModel flag cannot: whether the ACTIVE model id, which may be an agent-pinned
    // model different from the turn's, is an external endpoint outside the trust boundary.
    private readonly IModelTrustResolver _modelTrustResolver;

    // This provider is a SINGLETON but the custom-tool catalog is SCOPED (DbContext-backed), so it is resolved from a
    // fresh scope per offer call rather than captured — the established singleton→scoped-store pattern in this codebase.
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AllowedToolDto _spawnOfferDto;
    private readonly AllowedToolDto _computeOfferDto;
    private readonly AllowedToolDto _agentHomeOfferDto;
    private readonly AllowedToolDto _emitOutputOfferDto;

    // The work-session state tools, held out of the whole offer as profile-opt-in only. Each handler also fails closed
    // outside a session, so this projection is convenience, not the boundary.
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

        // The read-only coder tools are worker-owned IClientLocalToolHandlers, which GetLocalChatToolDescriptors does
        // NOT project, so they are merged here or the agent-send intersection with AllowedToolNames would be empty.
        var coderDescriptors = CoderToolDefinition.Descriptors;

        // The read-only knowledge-base tools (search_knowledge_base / read_document / read_surrounding_chunks) are also
        // worker-owned IClientLocalToolHandlers, merged the same way as the coder tools so they appear in the OFFER seam.
        var knowledgeDescriptors = KnowledgeToolCatalog.Descriptors;

        // ask_user needs the same OFFER-seam merge and is capability-gated with the coder and knowledge tools, but it is
        // NOT locality-gated and its RequiresApproval is structural. See docs/wiki/05-chat.md, "ask_user in the offer".
        var askUserDescriptor = new LocalChatToolDescriptor
        {
            Name = AskUserTool.ToolName,
            Description = AskUserTool.Description,
            ParameterSchema = AskUserTool.ParameterSchema,
            RequiresApproval = true,
            Category = ToolCategory.ReadLocal
        };

        // Each tool's Id is derived deterministically from its name so the offer list is byte-identical across sends.
        // spawn_subagent, run_python and run_in_agent_home are profile-opt-in only and stay out of this whole offer.
        _builtinAllTools =
        [
            .. builtinDescriptors.Select(static descriptor => ToOfferDto(descriptor.Name, descriptor.ParameterSchema, descriptor.RequiresApproval, descriptor.Category)),
            .. coderDescriptors.Select(static descriptor => ToOfferDto(descriptor.Name, descriptor.ParameterSchema, descriptor.RequiresApproval, descriptor.Category)),
            .. knowledgeDescriptors.Select(static descriptor => ToOfferDto(descriptor.Name, descriptor.ParameterSchema, descriptor.RequiresApproval, descriptor.Category)),
            ToOfferDto(askUserDescriptor.Name, askUserDescriptor.ParameterSchema, askUserDescriptor.RequiresApproval, askUserDescriptor.Category)
        ];

        // The provider-locality-gated variant: knowledge-base read tools AND coder workspace file tools removed, offered
        // to a cloud model unless AllowCloudModelAccess, so neither content class leaves through a tool result.
        var localDataToolNames = knowledgeDescriptors.Select(static descriptor => descriptor.Name)
                                                     .Concat(coderDescriptors.Select(static descriptor => descriptor.Name))
                                                     .ToHashSet(StringComparer.Ordinal);
        _builtinAllToolsNoLocalData =
        [
            .. _builtinAllTools.Where(tool => !localDataToolNames.Contains(tool.Name))
        ];

        // spawn_subagent is offered ONLY to an agent profile that opts in via AllowedToolNames, so it is held out of the
        // whole offer and added back by GetOfferedToolsForProfile alone: an unattended chat turn loads no other model.
        _spawnOfferDto = ToOfferDto(SpawnSubAgentToolDefinition.ToolName, SpawnSubAgentToolDefinition.ParameterSchema, requiresApproval: false, ToolCategory.Orchestration);

        // run_python takes the same profile-opt-in treatment for a sharper reason: it executes model-authored code on
        // the node. WriteExecute plus RequiresApproval is also what makes the unattended paths strip it for free.
        _computeOfferDto = ToOfferDto(ComputeToolDefinition.ToolName, ComputeToolDefinition.ParameterSchema, requiresApproval: true, ToolCategory.WriteExecute);

        // run_in_agent_home is profile-opt-in like run_python, which also keeps the deepest schema out of the GBNF grammar
        // llama.cpp compiles per turn. AgentHome:Enabled is enforced at EXECUTION, so the offer stays a static projection.
        _agentHomeOfferDto = ToOfferDto(AgentHomeToolDefinition.ToolName, AgentHomeToolDefinition.ParameterSchema, requiresApproval: true, ToolCategory.WriteExecute);

        // emit_output is held out of EVERY projection, so only the integration coordinator can union it in and only it
        // can recompose the raw approval flag through IToolApprovalPolicy: a property of the RUN, not of an agent.
        _emitOutputOfferDto = ToOfferDto(EmitOutputToolDefinition.ToolName,
            EmitOutputToolDefinition.ParameterSchema,
            requiresApproval: false,
            ToolCategory.ReadLocal);

        // WriteExecute is the honest category: every one of these writes durable session rows, and hiding that from a
        // category-based operator policy would blind the layer whose job is to see it.
        _workSessionOfferDtos =
            [.. WorkSessionToolCatalog.Descriptors.Select(static descriptor => ToOfferDto(descriptor.Name, descriptor.ParameterSchema, descriptor.RequiresApproval, descriptor.Category))];

        // The capability-gated variant, precomputed once: the built-ins minus the coder and knowledge tools and
        // ask_user, returned when the active model is not tool-capable.
        var capableOnlyNames = coderDescriptors.Select(static descriptor => descriptor.Name)
                                               .Concat(knowledgeDescriptors.Select(static descriptor => descriptor.Name))
                                               .Append(askUserDescriptor.Name)
                                               .ToHashSet(StringComparer.Ordinal);
        _builtinAllToolsNonCapable =
        [
            .. _builtinAllTools.Where(tool => !capableOnlyNames.Contains(tool.Name))
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
                // run_python runs commands on the node, the category that drives the picker's danger badge. Listing it
                // ungated by model is what lets an operator add it to a profile's AllowedToolNames at all.
                Category = ToolCategory.WriteExecute
            },
            new LocalToolCatalogEntry
            {
                Name = AgentHomeToolDefinition.ToolName,
                Description = AgentHomeToolDefinition.Description,
                RequiresApproval = true,
                Source = BuiltinSource,
                // Same reasoning as run_python's entry above: listed UNGATED by model so the picker shows it and CRUD
                // validation accepts the name, the only way an operator can opt an agent in.
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
            AgentHomeToolDefinition.ToolName,
            .. WorkSessionToolCatalog.Descriptors.Select(static descriptor => descriptor.Name)
        ];
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The allow-list is read LIVE on every offer, never captured at composition, so an operator's Node Settings
    ///     edit applies to the next turn without a restart and this seam agrees with the two other consumers that
    ///     already read it per request, <c>GetToolCapableModelsEndpoint</c> and
    ///     <c>OrchestrationResolver.BuildToolCapableSetAsync</c>. The read is affordable because
    ///     <c>CachedNodeSettingsStore.Load</c> is a memory-cache hit that <c>SaveAsync</c> re-primes.
    /// </remarks>
    public bool IsToolCapable(string? activeModelId)
    {
        if (activeModelId is null)
        {
            return false;
        }

        // Ordinal and case-SENSITIVE, matching the allow-list's exact-match contract — a model differing only by case
        // is not capable — and the identical construction in OrchestrationResolver.BuildToolCapableSetAsync.
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
        // High-risk tools (coder, knowledge, ask_user and every MCP tool) go only to a tool-capable model, and a null or
        // unknown id counts as not capable. The MCP part is read live and sorted, so one catalog state yields one offer.
        var capable = IsToolCapable(activeModelId);
        if (!capable)
        {
            // The non-capable variant already excludes the knowledge tools (they are capable-only), so it needs no
            // locality gate.
            return _builtinAllToolsNonCapable;
        }

        // Provider-locality gate: the node-local-data tools are withheld from a cloud model unless the operator opted
        // in. The Codex-catalog and external-trust checks catch a pinned cloud id even on a locally-routed turn.
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
#pragma warning disable MA0042 // The synchronous overload IS this method's base: it is the pure in-memory built-in + MCP view, which the async overload extends with the custom-tool store read. Calling the async twin here recurses.
        var baseOffer = GetOfferedTools(activeModelId, isCloudModel);
#pragma warning restore MA0042

        // Custom tools merge ONLY in the tool-capable branch and ONLY for a node-local model: a custom command or fetch
        // tool reaches local data and the host, so a cloud model never gets one, whatever the knowledge opt-in says.
        if (!IsToolCapable(activeModelId) || IsOutsideTrustBoundary(activeModelId, isCloudModel))
        {
            return baseOffer;
        }

        // The node kill-switch is off by default. It is checked here, before the scope and store read, so the common
        // disabled path stays cheap.
        if (!await _runtimeSettings.GetCustomToolsEnabledAsync(cancellationToken))
        {
            return baseOffer;
        }

        var customDescriptors = await GetEnabledCustomDescriptorsAsync(cancellationToken);
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
    public IReadOnlyList<AllowedToolDto> GetIntegrationOutputOffer() =>
        [_emitOutputOfferDto];

    public IReadOnlyList<AllowedToolDto> GetOfferedToolsForProfile(string? activeModelId, bool isCloudModel = false)
    {
        // The profile-intersection pool: the whole offer PLUS the opt-in-only tools, still capability-gated, so a
        // non-capable model gets the non-capable variant and no spawn tool and the opt-in cannot bypass the gate.
        var capable = IsToolCapable(activeModelId);
        if (!capable)
        {
            return _builtinAllToolsNonCapable;
        }

        return
        [
            .. GetOfferedTools(activeModelId, isCloudModel),
            .. SpawnOffer(activeModelId, isCloudModel),
            .. ComputeOffer(activeModelId, isCloudModel),
            .. AgentHomeOffer(activeModelId, isCloudModel),
            .. _workSessionOfferDtos
        ];
    }

    public async Task<IReadOnlyList<AllowedToolDto>> GetOfferedToolsForProfileAsync(string? activeModelId, bool isCloudModel, CancellationToken cancellationToken = default)
    {
        // Non-capable models get the non-capable variant and NO spawn/custom tools, so the opt-in cannot bypass the gate.
        if (!IsToolCapable(activeModelId))
        {
            return _builtinAllToolsNonCapable;
        }

        // The async whole offer (built-in + MCP + capability/local-gated custom) PLUS the opt-in-only spawn tool — the same
        // asymmetry as the synchronous GetOfferedToolsForProfile, with custom tools folded in through GetOfferedToolsAsync.
        return
        [
            .. await GetOfferedToolsAsync(activeModelId, isCloudModel, cancellationToken),
            .. SpawnOffer(activeModelId, isCloudModel),
            .. ComputeOffer(activeModelId, isCloudModel),
            .. AgentHomeOffer(activeModelId, isCloudModel),
            .. _workSessionOfferDtos
        ];
    }

    /// <summary>
    ///     <c>spawn_subagent</c> for the profile pool, or nothing for a model outside the trust boundary.
    /// </summary>
    /// <remarks>
    ///     Spawning is DELEGATION: the child resolves its own model and tool set, so an ungated spawn offer would let
    ///     a parent bind a child to a node-local model, have IT read the withheld data and take the result back —
    ///     a bypass of all three direct gates rather than a capability of its own. It is withheld unconditionally
    ///     rather than behind <c>AllowCloudModelAccess</c>, which cannot be given informedly about data a child agent
    ///     fetches on its own initiative.
    /// </remarks>
    private IReadOnlyList<AllowedToolDto> SpawnOffer(string? activeModelId, bool isCloudModel)
    {
        return IsOutsideTrustBoundary(activeModelId, isCloudModel) ? [] : [_spawnOfferDto];
    }

    /// <summary>
    ///     <c>run_python</c> for the profile pool, or nothing for a cloud-hosted model.
    /// </summary>
    /// <remarks>
    ///     The gate here is not the content-leak rationale of the knowledge and coder tools: what is withheld is a
    ///     REMOTE model's ability to direct code execution on the operator's machine, the same concern that put bare
    ///     interpreters on <c>HostExecutableGuard</c>'s denylist. It is therefore unconditional rather than behind
    ///     <c>AllowCloudModelAccess</c>, which governs only reading node-local data.
    /// </remarks>
    private IReadOnlyList<AllowedToolDto> ComputeOffer(string? activeModelId, bool isCloudModel)
    {
        return IsOutsideTrustBoundary(activeModelId, isCloudModel) ? [] : [_computeOfferDto];
    }

    /// <summary>
    ///     <c>run_in_agent_home</c> for the profile pool, or nothing for a model outside the trust boundary.
    /// </summary>
    /// <remarks>
    ///     Withheld on exactly <c>run_python</c>'s reasoning at a larger blast radius: <c>run_commands</c> and
    ///     <c>write_workspace</c> execute in a node-local sandbox and <c>export_patch</c> produces a diff aimed at the
    ///     operator's own folders. That is a remote model directing execution on the operator's machine, so it is
    ///     unconditional rather than behind <c>AllowCloudModelAccess</c>.
    /// </remarks>
    private IReadOnlyList<AllowedToolDto> AgentHomeOffer(string? activeModelId, bool isCloudModel)
    {
        return IsOutsideTrustBoundary(activeModelId, isCloudModel) ? [] : [_agentHomeOfferDto];
    }

    /// <summary>
    ///     Whether prompts for <paramref name="activeModelId" /> leave the node, for the profile-pool tool gates above.
    /// </summary>
    /// <remarks>
    ///     The formula is the turn's own cloud flag, OR a Codex-pinned id, OR an external id whose declared locality is
    ///     anything other than Local. That last clause is deliberately not "is declared Cloud": an unreadable
    ///     registration resolves UNRESOLVED, and only a positively resolved local declaration earns local privileges.
    ///     It is synchronous by necessity — the offer seam has no async boundary — and answers from the registry's
    ///     cached generation, whose only unprimed window is before the node has finished booting.
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
        var customDescriptors = await GetEnabledCustomDescriptorsAsync(cancellationToken);
#pragma warning disable MA0042 // The synchronous overload IS this method's base: it is the pure in-memory built-in + MCP view, which the async overload extends with the custom-tool store read. Calling the async twin here recurses.
        if (customDescriptors.Count == 0)
        {
            return GetKnownToolNames();
        }

        return
        [
            .. GetKnownToolNames(),
            .. customDescriptors.Select(static descriptor => descriptor.Name)
        ];
#pragma warning restore MA0042
    }

    public async Task<IReadOnlyList<LocalToolCatalogEntry>> GetKnownToolsAsync(CancellationToken cancellationToken = default)
    {
        var customDescriptors = await GetEnabledCustomDescriptorsAsync(cancellationToken);
#pragma warning disable MA0042 // The synchronous overload IS this method's base: it is the pure in-memory built-in + MCP view, which the async overload extends with the custom-tool store read. Calling the async twin here recurses.
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
#pragma warning restore MA0042
    }

    // Reads the enabled, acknowledged custom-tool descriptors LIVE through a fresh scope, since this provider is a
    // singleton. The catalog leaves the node kill-switch out, so each OFFER method applies it before calling here.
    private async Task<IReadOnlyList<LocalChatToolDescriptor>> GetEnabledCustomDescriptorsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ICustomToolCatalog>();
        return await catalog.GetDescriptorsAsync(cancellationToken);
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
    ///     <c>mcp:{slug}</c>; an unexpected shape falls back to the bare <c>mcp</c> tag.
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
