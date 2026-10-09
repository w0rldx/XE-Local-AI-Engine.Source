namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
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
using XE_Local_AI_Engine.Client.Services.WebAccess;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;
using XE_Local_AI_Engine.Providers.Abstractions.External;

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

    // The whole capable offer minus the knowledge-base tools, returned while the node switch turns those tools off.
    private readonly IReadOnlyList<AllowedToolDto> _builtinAllToolsNoKnowledge;
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

    // The built-in web tools, merged into the WHOLE offer only while WebAccessEnabled is on (ADR 0017).
    private readonly IReadOnlyList<AllowedToolDto> _webAccessOfferDtos;
    private readonly ILogger<LocalToolOfferProvider> _logger;

    public LocalToolOfferProvider(IAgentToolRegistry toolRegistry,
        IMcpToolRegistry mcpToolRegistry,
        INodeRuntimeSettings runtimeSettings,
        IServiceScopeFactory scopeFactory,
        IModelTrustResolver modelTrustResolver,
        ILogger<LocalToolOfferProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(toolRegistry);
        _logger = logger ?? NullLogger<LocalToolOfferProvider>.Instance;
        _mcpToolRegistry = mcpToolRegistry ?? throw new ArgumentNullException(nameof(mcpToolRegistry));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _modelTrustResolver = modelTrustResolver ?? throw new ArgumentNullException(nameof(modelTrustResolver));

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
            .. builtinDescriptors.Select(static descriptor => ToOfferDto(descriptor.Name, descriptor.Description, descriptor.ParameterSchema, descriptor.RequiresApproval, descriptor.Category)),
            .. coderDescriptors.Select(static descriptor => ToOfferDto(descriptor.Name, descriptor.Description, descriptor.ParameterSchema, descriptor.RequiresApproval, descriptor.Category)),
            .. knowledgeDescriptors.Select(static descriptor => ToOfferDto(descriptor.Name, descriptor.Description, descriptor.ParameterSchema, descriptor.RequiresApproval, descriptor.Category)),
            ToOfferDto(askUserDescriptor.Name, askUserDescriptor.Description, askUserDescriptor.ParameterSchema, askUserDescriptor.RequiresApproval, askUserDescriptor.Category)
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
        var knowledgeToolNames = knowledgeDescriptors.Select(static descriptor => descriptor.Name).ToHashSet(StringComparer.Ordinal);
        _builtinAllToolsNoKnowledge =
        [
            .. _builtinAllTools.Where(tool => !knowledgeToolNames.Contains(tool.Name))
        ];

        // spawn_subagent is offered ONLY to an agent profile that opts in via AllowedToolNames, so it is held out of the
        // whole offer and added back by GetOfferedToolsForProfile alone: an unattended chat turn loads no other model.
        _spawnOfferDto = ToOfferDto(SpawnSubAgentToolDefinition.ToolName, SpawnSubAgentToolDefinition.Description, SpawnSubAgentToolDefinition.ParameterSchema, requiresApproval: false,
            ToolCategory.Orchestration);

        // run_python takes the same profile-opt-in treatment for a sharper reason: it executes model-authored code on
        // the node. WriteExecute plus RequiresApproval is also what makes the unattended paths strip it for free.
        _computeOfferDto = ToOfferDto(ComputeToolDefinition.ToolName, ComputeToolDefinition.Description, ComputeToolDefinition.ParameterSchema, requiresApproval: true, ToolCategory.WriteExecute);

        // run_in_agent_home is profile-opt-in like run_python, which also keeps the deepest schema out of the GBNF grammar
        // llama.cpp compiles per turn. AgentHome:Enabled is enforced at EXECUTION, so the offer stays a static projection.
        _agentHomeOfferDto = ToOfferDto(AgentHomeToolDefinition.ToolName, AgentHomeToolDefinition.Description, AgentHomeToolDefinition.ParameterSchema, requiresApproval: true,
            ToolCategory.WriteExecute);

        // emit_output is held out of EVERY projection, so only the integration coordinator can union it in and only it
        // can recompose the raw approval flag through IToolApprovalPolicy: a property of the RUN, not of an agent.
        _emitOutputOfferDto = ToOfferDto(EmitOutputToolDefinition.ToolName,
            EmitOutputToolDefinition.Description,
            EmitOutputToolDefinition.ParameterSchema,
            requiresApproval: false,
            ToolCategory.ReadLocal);

        // WriteExecute is the honest category: every one of these writes durable session rows, and hiding that from a
        // category-based operator policy would blind the layer whose job is to see it.
        _workSessionOfferDtos =
        [
            .. WorkSessionToolCatalog.Descriptors.Select(static descriptor =>
                ToOfferDto(descriptor.Name, descriptor.Description, descriptor.ParameterSchema, descriptor.RequiresApproval, descriptor.Category))
        ];

        _webAccessOfferDtos =
        [
            .. WebAccessToolCatalog.Descriptors.Select(static descriptor =>
                ToOfferDto(descriptor.Name, descriptor.Description, descriptor.ParameterSchema, descriptor.RequiresApproval, descriptor.Category))
        ];

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
                // Picker text only; the model never sees this entry, and is offered the tool only where it can run. Off Linux
                // this suffix is where an operator learns that a profile selecting it gets no tool on this node.
                Description = OperatingSystem.IsLinux()
                    ? ComputeToolDefinition.Description
                    : ComputeToolDefinition.Description + " (Linux only: not available on this node, an agent selecting it gets no tool)",
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
            }),
            // Listed whatever the node switch says, so the agent picker shows them and AllowedToolNames validation accepts them.
            .. WebAccessToolCatalog.Descriptors.Select(static descriptor => new LocalToolCatalogEntry
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
            .. WorkSessionToolCatalog.Descriptors.Select(static descriptor => descriptor.Name),
            .. WebAccessToolCatalog.Descriptors.Select(static descriptor => descriptor.Name)
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

    public IReadOnlyList<AllowedToolDto> GetOfferedTools(string? activeModelId, bool isCloudModel = false, ExternalProviderCloudGrants? cloudGrants = null)
    {
        // High-risk tools (coder, knowledge, ask_user and every MCP tool) go only to a tool-capable model, and a null or
        // unknown id counts as not capable. The non-capable variant excludes the knowledge tools, so needs no locality gate.
        return IsToolCapable(activeModelId) ? CapableOffer(activeModelId, isCloudModel, ResolveGrants(activeModelId, cloudGrants)) : _builtinAllToolsNonCapable;
    }

    // The tool-capable whole offer under already-resolved grants. The MCP part is read live and sorted, so one catalog
    // state yields one offer.
#pragma warning disable MA0045 // The whole offer is the synchronous base of every offer, which has no async boundary; the sync twins are the designated per-offer reads.
    private IReadOnlyList<AllowedToolDto> CapableOffer(string? activeModelId, bool isCloudModel, ExternalProviderCloudGrants grants)
    {
        // Provider-locality gate: node-local-data tools are withheld from a cloud model unless opted in; the trust checks catch
        // a pinned cloud id on a locally-routed turn. Both node switches are read per offer, so a save applies to the next turn.
        var baseOffer = _runtimeSettings.GetKnowledgeAgentToolsEnabled() ? _builtinAllTools : _builtinAllToolsNoKnowledge;
        var leavesNode = LeavesNode(activeModelId, isCloudModel);
        // A connection's own grant stands in for the node-wide switch of its class; it never changes leavesNode.
        if (leavesNode && !grants.LocalData && !_runtimeSettings.GetAllowCloudModelAccess())
        {
            baseOffer = _builtinAllToolsNoLocalData;
        }

        var mcpDescriptors = _mcpToolRegistry.GetDescriptors();
        if (mcpDescriptors.Count == 0)
        {
            return baseOffer;
        }

        // An MCP server reaches whatever its host can, so a model outside the trust boundary gets its tools only by opt-in.
        if (leavesNode && !grants.McpTools && !_runtimeSettings.GetAllowCloudModelMcpTools())
        {
            _logger.LogDebug("MCP tools withheld from the offer: the model leaves the node and AllowCloudModelMcpTools is off (Node Settings, Privacy & updates section).");
            return baseOffer;
        }

        return
        [
            .. baseOffer,
            .. mcpDescriptors.Select(static descriptor => ToOfferDto(descriptor.Name, descriptor.Description, descriptor.ParameterSchema, descriptor.RequiresApproval, descriptor.Category))
        ];
    }
#pragma warning restore MA0045

    public Task<IReadOnlyList<AllowedToolDto>> GetOfferedToolsAsync(string? activeModelId, bool isCloudModel, ExternalProviderCloudGrants? cloudGrants = null,
        CancellationToken cancellationToken = default)
    {
        // Custom and web tools merge ONLY in the tool-capable branch.
        return IsToolCapable(activeModelId)
            ? CapableOfferAsync(activeModelId, isCloudModel, ResolveGrants(activeModelId, cloudGrants), cancellationToken)
            : Task.FromResult(_builtinAllToolsNonCapable);
    }

    // The tool-capable whole offer plus web and custom tools. A model outside the trust boundary gets the web tools and
    // HttpFetch custom tools only by opt-in, and a Command custom tool (host execution) never.
    private async Task<IReadOnlyList<AllowedToolDto>> CapableOfferAsync(string? activeModelId, bool isCloudModel, ExternalProviderCloudGrants grants,
        CancellationToken cancellationToken)
    {
#pragma warning disable MA0042 // The synchronous core IS this method's base: it is the pure in-memory built-in + MCP view, which the async overload extends with the custom-tool store read. Calling the async twin here recurses.
        var baseOffer = CapableOffer(activeModelId, isCloudModel, grants);
#pragma warning restore MA0042

        var leavesNode = LeavesNode(activeModelId, isCloudModel);
        var cloudWebAllowed = !leavesNode || grants.WebTools || await _runtimeSettings.GetAllowCloudModelWebToolsAsync(cancellationToken);
        var webAccessEnabled = await _runtimeSettings.GetWebAccessEnabledAsync(cancellationToken);

        // A bound agent still gets the web tools only through its AllowedToolNames intersection.
        if (!webAccessEnabled)
        {
            // Debug, not Information: this runs on every offer. It is the line that explains an agent whose web tools
            // were "dropped" from its allowed set while the node switch is off.
            _logger.LogDebug("Web tools withheld from the offer: WebAccessEnabled is off (Node Settings, Knowledge section).");
        }
        else if (!cloudWebAllowed)
        {
            _logger.LogDebug("Web tools withheld from the offer: the model leaves the node and AllowCloudModelWebTools is off (Node Settings, Privacy & updates section).");
        }
        else
        {
            baseOffer = [.. baseOffer, .. _webAccessOfferDtos];
        }

        // The node kill-switch is off by default. It is checked here, before the scope and store read, so the common
        // disabled path stays cheap.
        if (!await _runtimeSettings.GetCustomToolsEnabledAsync(cancellationToken))
        {
            return baseOffer;
        }

        // Outside the trust boundary a custom tool is outbound reach, so it takes the web tools' two switches together.
        if (leavesNode && !(webAccessEnabled && cloudWebAllowed))
        {
            _logger.LogDebug("Custom tools withheld from the offer: the model leaves the node and WebAccessEnabled or AllowCloudModelWebTools is off (Node Settings).");
            return baseOffer;
        }

        var customDescriptors = await GetEnabledCustomDescriptorsAsync(cancellationToken);

        // The descriptor carries no kind, so the category stands in: CustomToolCatalog maps HttpFetch to Network alone.
        if (leavesNode)
        {
            var allCustomCount = customDescriptors.Count;
            customDescriptors = [.. customDescriptors.Where(static descriptor => descriptor.Category == ToolCategory.Network)];
            if (customDescriptors.Count < allCustomCount)
            {
                _logger.LogDebug("Command custom tools withheld from the offer: a model that leaves the node never gets one.");
            }
        }

        if (customDescriptors.Count == 0)
        {
            return baseOffer;
        }

        return
        [
            .. baseOffer,
            .. customDescriptors.Select(static descriptor => ToOfferDto(descriptor.Name, descriptor.Description, descriptor.ParameterSchema, descriptor.RequiresApproval, descriptor.Category))
        ];
    }

    /// <inheritdoc />
    public IReadOnlyList<AllowedToolDto> GetIntegrationOutputOffer() =>
        [_emitOutputOfferDto];

    public IReadOnlyList<AllowedToolDto> GetOfferedToolsForProfile(string? activeModelId, bool isCloudModel = false, ExternalProviderCloudGrants? cloudGrants = null)
    {
        // The profile-intersection pool: the whole offer PLUS the opt-in-only tools, still capability-gated, so a
        // non-capable model gets the non-capable variant and no spawn tool and the opt-in cannot bypass the gate.
        var capable = IsToolCapable(activeModelId);
        if (!capable)
        {
            return _builtinAllToolsNonCapable;
        }

        var grants = ResolveGrants(activeModelId, cloudGrants);
        return
        [
            .. CapableOffer(activeModelId, isCloudModel, grants),
            .. SpawnOffer(activeModelId, isCloudModel, grants),
            .. ComputeOffer(activeModelId, isCloudModel),
            .. AgentHomeOffer(activeModelId, isCloudModel),
            .. _workSessionOfferDtos
        ];
    }

    public async Task<IReadOnlyList<AllowedToolDto>> GetOfferedToolsForProfileAsync(string? activeModelId, bool isCloudModel, ExternalProviderCloudGrants? cloudGrants = null,
        CancellationToken cancellationToken = default)
    {
        // Non-capable models get the non-capable variant and NO spawn/custom tools, so the opt-in cannot bypass the gate.
        if (!IsToolCapable(activeModelId))
        {
            return _builtinAllToolsNonCapable;
        }

        // The async whole offer (built-in + MCP + capability/local-gated custom) PLUS the opt-in-only spawn tool — the same
        // asymmetry as the synchronous GetOfferedToolsForProfile, with custom tools folded in through CapableOfferAsync.
        var grants = ResolveGrants(activeModelId, cloudGrants);
        return
        [
            .. await CapableOfferAsync(activeModelId, isCloudModel, grants, cancellationToken),
            .. SpawnOffer(activeModelId, isCloudModel, grants),
            .. ComputeOffer(activeModelId, isCloudModel),
            .. AgentHomeOffer(activeModelId, isCloudModel),
            .. _workSessionOfferDtos
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CloudWithheldTool>> GetCloudWithheldToolsAsync(string? activeModelId, bool isCloudModel, ExternalProviderCloudGrants? cloudGrants = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsToolCapable(activeModelId) || !LeavesNode(activeModelId, isCloudModel))
        {
            return [];
        }

        // The same grant and switch reads, in the same order of precedence, as the offer methods above.
        var grants = ResolveGrants(activeModelId, cloudGrants);
        var withheld = new List<CloudWithheldTool>();
        if (!grants.McpTools && !await _runtimeSettings.GetAllowCloudModelMcpToolsAsync(cancellationToken))
        {
            withheld.AddRange(_mcpToolRegistry.GetDescriptors().Select(static descriptor => Withheld(descriptor.Name, CloudToolSwitch.McpTools)));
        }

        if (!grants.WebTools && await _runtimeSettings.GetWebAccessEnabledAsync(cancellationToken) && !await _runtimeSettings.GetAllowCloudModelWebToolsAsync(cancellationToken))
        {
            withheld.AddRange(_webAccessOfferDtos.Select(static tool => Withheld(tool.Name, CloudToolSwitch.WebTools)));
            if (await _runtimeSettings.GetCustomToolsEnabledAsync(cancellationToken))
            {
                var customDescriptors = await GetEnabledCustomDescriptorsAsync(cancellationToken);
                withheld.AddRange(customDescriptors.Where(static descriptor => descriptor.Category == ToolCategory.Network)
                                                   .Select(static descriptor => Withheld(descriptor.Name, CloudToolSwitch.WebTools)));
            }
        }

        if (!grants.SubAgents && !await _runtimeSettings.GetAllowCloudModelSubAgentsAsync(cancellationToken))
        {
            withheld.Add(Withheld(SpawnSubAgentToolDefinition.ToolName, CloudToolSwitch.SubAgents));
        }

        return withheld;
    }

    private static CloudWithheldTool Withheld(string name, CloudToolSwitch toolSwitch) =>
        new()
        {
            Name = name,
            Switch = toolSwitch
        };

    /// <summary>
    ///     <c>spawn_subagent</c> for the profile pool, or nothing for a model outside the trust boundary unless allowed.
    /// </summary>
    /// <remarks>
    ///     Spawning is DELEGATION: the child resolves its own model and tool set, so a spawn offer lets a parent bind a
    ///     child to a node-local model, have IT read the withheld data and take the result back — a bypass of all three
    ///     direct gates. It therefore sits behind its own switch, <c>AllowCloudModelSubAgents</c>, not behind
    ///     <c>AllowCloudModelAccess</c>, which cannot be given informedly about data a child fetches on its own.
    /// </remarks>
    private IReadOnlyList<AllowedToolDto> SpawnOffer(string? activeModelId, bool isCloudModel, ExternalProviderCloudGrants grants)
    {
#pragma warning disable MA0045 // The spawn offer is part of the synchronous profile pool, which has no async boundary; the sync twin is the designated per-offer read.
        if (!LeavesNode(activeModelId, isCloudModel) || grants.SubAgents || _runtimeSettings.GetAllowCloudModelSubAgents())
#pragma warning restore MA0045
        {
            return [_spawnOfferDto];
        }

        _logger.LogDebug("spawn_subagent withheld from the offer: the model leaves the node and AllowCloudModelSubAgents is off (Node Settings, Privacy & updates section).");
        return [];
    }

    /// <summary>
    ///     <c>run_python</c> for the profile pool, or nothing for a cloud-hosted model or a node off Linux.
    /// </summary>
    /// <remarks>
    ///     The gate here is not the content-leak rationale of the knowledge and coder tools: what is withheld is a
    ///     REMOTE model's ability to direct code execution on the operator's machine, the same concern that put bare
    ///     interpreters on <c>HostExecutableGuard</c>'s denylist. It is therefore unconditional rather than behind
    ///     <c>AllowCloudModelAccess</c>, which governs only reading node-local data. The managed Python runtime exists
    ///     only on Linux, so elsewhere every call would fail at execution; it is not offered there at all.
    /// </remarks>
    private IReadOnlyList<AllowedToolDto> ComputeOffer(string? activeModelId, bool isCloudModel)
    {
        return !OperatingSystem.IsLinux() || LeavesNode(activeModelId, isCloudModel) ? [] : [_computeOfferDto];
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
        return LeavesNode(activeModelId, isCloudModel) ? [] : [_agentHomeOfferDto];
    }

    /// <summary>
    ///     Whether prompts for <paramref name="activeModelId" /> leave the node, for the profile-pool tool gates above.
    /// </summary>
    /// <remarks>
    ///     The turn's own cloud flag, OR anything but a positive Local from <see cref="IModelTrustResolver.Classify" />,
    ///     so an unresolved id earns no local privileges. Synchronous by necessity: the offer seam has no async boundary.
    /// </remarks>
    private bool LeavesNode(string? activeModelId, bool isCloudModel)
    {
        return isCloudModel || _modelTrustResolver.Classify(activeModelId) != ModelTrustLocality.Local;
    }

    // The ONE grant read of a public offer call: the caller's snapshot grants, else the resolver's cached answer.
    private ExternalProviderCloudGrants ResolveGrants(string? activeModelId, ExternalProviderCloudGrants? cloudGrants)
    {
        return cloudGrants ?? _modelTrustResolver.ClassifyCloudGrants(activeModelId);
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

    // The description is the one the executable tool shows the model; the budgets count it (RuntimePackageConfigHash.Compute leaves it out).
    private static AllowedToolDto ToOfferDto(string name, string? description, string? parameterSchema, bool requiresApproval, ToolCategory category)
    {
        return new AllowedToolDto
        {
            Id = DeriveDeterministicId(name),
            Name = name,
            Location = ToolLocation.ClientLocal,
            Description = description,
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
