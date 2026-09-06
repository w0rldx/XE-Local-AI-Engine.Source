namespace XE_Local_AI_Engine.Client.Services.Capacity;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Instructions;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Default <see cref="ISubAgentSpawnService" />. Resolves the sub-agent's model binding + curated tool set, enforces
///     the depth/fan-out/cloud-spawn caps, calls the capacity gate, and dispatches per verdict. An admitted spawn runs a
///     <see cref="ChatClientAgent" /> as an <see cref="AIFunction" /> over the SAME production-decorated
///     <see cref="IChatClient" /> (cloud/local routed per send by the runtime client).
///     <para>
///         A profile-bound child consumes the SAME complete <see cref="ResolvedAgentRuntime" /> a direct agent send does
///         — resolved once — so it inherits the resolved system prompt (scaffold + persona + injected playbook memory),
///         reasoning effort (gated on the child model's own thinking capability, mirroring
///         <see cref="ParticipantReasoningOptions" />), skills (MAF progressive disclosure), AND its curated tools, not
///         just the tool set. This is structurally the orchestration-participant path: an agent-as-tool never receives
///         the outer runner's per-run <c>RunOptions</c>, so reasoning + skills must be baked into the agent at
///         construction. A model-id-only child (no profile) stays as-is: raw request instructions, tool-less, no
///         reasoning/skills. Post-run adaptive-memory EXTRACTION stays disabled for a child — an intentional restriction
///         (see <c>docs/agent-knowledge.md</c>).
///     </para>
///     <para>
///         Depth cap (≤ 2) is enforced two ways: (1) PRIMARY, STRUCTURAL — the child's tool set has
///         <c>spawn_subagent</c> filtered out UNCONDITIONALLY, so a child can never spawn; (2) defense-in-depth —
///         <see cref="SpawnContext.Depth" /> ≥ 1 short-circuits to a sanitized reject. A profile-bound child inherits its
///         own definition's curated tools (offer ∩ AllowedToolNames, minus <c>spawn_subagent</c> AND any approval-gated
///         tool — a child has no HITL route to answer an approval request); a model-id-only child (no profile, no
///         AllowedToolNames) is tool-less. Every expected rejection is a sanitized string, not an exception.
///     </para>
/// </summary>
internal sealed partial class SubAgentSpawnService : ISubAgentSpawnService, IMcpAgentExecutionService
{
    // The inner agent-as-tool exposes a single "query" input parameter (re-verified against MAF 1.15.0
    // AIAgentExtensions.AsAIFunction); the spawn task is passed under
    // that key. AsAIFunction forwards the outer CancellationToken into the inner run (verified by
    // Spawn_PropagatesCancellationToInnerRun), so no linked CTS is needed — the parent ct flows straight through
    // InvokeAsync.
    private const string InnerAgentInputKey = "query";
    private const string SubAgentName = "sub-agent";
    private const string SubAgentDescription = "A spawned sub-agent that answers a delegated task.";

    // The persona half only; BaseInstructionComposer prepends the same versioned scaffold every other resolved
    // agent gets, so a model-id-only child (no persisted definition to opt out) gets the same grounding/tool/output
    // discipline as a bound one.
    private const string DefaultSubAgentPersonaInstructions =
        "You are a focused sub-agent. Complete the delegated task and return a concise result.";

    // Sanitized, caller-facing constants — never interpolate a model id, path, definition name, or budget figure into a
    // reason handed back to the calling agent (its transcript is not a trusted sink for node-internal detail).
    private const string ReasonFanOutExceeded = "Cannot spawn: the maximum number of concurrent sub-agents for this turn is already running.";
    private const string ReasonCloudCapExceeded = "Cannot spawn: the maximum number of cloud sub-agents for this turn has been reached.";
    private const string ReasonSubAgentUnresolved = "Cannot spawn: the requested sub-agent or model could not be resolved.";
    private const string ReasonInvalidArguments = "Cannot spawn: provide a non-empty task and exactly one of subAgentKey or modelId.";
    private const string ReasonQueueBusy = "Cannot spawn right now: the target model is busy. Try again shortly.";
    private const string ReasonDepthExceeded = "Cannot spawn: a sub-agent may not spawn further sub-agents.";
    private const string ReasonParentOutsideTrustBoundary = "Cannot spawn: a model outside this node's trust boundary may not delegate to a sub-agent.";

    private readonly IAgentDefinitionResolver _agentDefinitionResolver;
    private readonly ICapacityService _capacityService;
    private readonly IChatClient _chatClient;
    private readonly IClientLocalToolRegistry _clientLocalToolRegistry;
    private readonly ICustomToolCatalog _customToolCatalog;
    private readonly IAgentDefinitionStore _definitionStore;
    private readonly IExternalProviderRegistry _externalProviderRegistry;
    private readonly IAgentInstructionProvider _instructionProvider;
    private readonly ILogger<SubAgentSpawnService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IMcpExecutionBindingResolver _mcpExecutionBindingResolver;
    private readonly IMcpAgenticToolAdapter _mcpAgenticToolAdapter;
    private readonly IMcpWorkspaceExecutionSessionFactory _mcpWorkspaceSessionFactory;
    private readonly IMcpToolRegistry _mcpToolRegistry;
    private readonly IModelCapabilityResolver _modelCapabilityResolver;
    private readonly IModelTrustResolver _modelTrustResolver;
    private readonly INodeSettingsStore _nodeSettingsStore;
    private readonly SpawnOptions _options;
    private readonly ISpawnSerializer _spawnSerializer;
    private readonly IAgentToolRegistry _toolRegistry;

    public SubAgentSpawnService(ICapacityService capacityService,
        ISpawnSerializer spawnSerializer,
        IAgentDefinitionStore definitionStore,
        IAgentDefinitionResolver agentDefinitionResolver,
        IAgentToolRegistry toolRegistry,
        IClientLocalToolRegistry clientLocalToolRegistry,
        IMcpToolRegistry mcpToolRegistry,
        ICustomToolCatalog customToolCatalog,
        IChatClient chatClient,
        IOptions<SpawnOptions> options,
        IAgentInstructionProvider instructionProvider,
        IModelCapabilityResolver modelCapabilityResolver,
        IModelTrustResolver modelTrustResolver,
        IMcpExecutionBindingResolver mcpExecutionBindingResolver,
        IMcpAgenticToolAdapter mcpAgenticToolAdapter,
        IMcpWorkspaceExecutionSessionFactory mcpWorkspaceSessionFactory,
        INodeSettingsStore nodeSettingsStore,
        IExternalProviderRegistry externalProviderRegistry,
        ILoggerFactory loggerFactory,
        ILogger<SubAgentSpawnService> logger)
    {
        _capacityService = capacityService ?? throw new ArgumentNullException(nameof(capacityService));
        _spawnSerializer = spawnSerializer ?? throw new ArgumentNullException(nameof(spawnSerializer));
        _definitionStore = definitionStore ?? throw new ArgumentNullException(nameof(definitionStore));
        _agentDefinitionResolver = agentDefinitionResolver ?? throw new ArgumentNullException(nameof(agentDefinitionResolver));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _clientLocalToolRegistry = clientLocalToolRegistry ?? throw new ArgumentNullException(nameof(clientLocalToolRegistry));
        _mcpToolRegistry = mcpToolRegistry ?? throw new ArgumentNullException(nameof(mcpToolRegistry));
        _customToolCatalog = customToolCatalog ?? throw new ArgumentNullException(nameof(customToolCatalog));
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _instructionProvider = instructionProvider ?? throw new ArgumentNullException(nameof(instructionProvider));
        _modelCapabilityResolver = modelCapabilityResolver ?? throw new ArgumentNullException(nameof(modelCapabilityResolver));
        _modelTrustResolver = modelTrustResolver ?? throw new ArgumentNullException(nameof(modelTrustResolver));
        _mcpExecutionBindingResolver = mcpExecutionBindingResolver ?? throw new ArgumentNullException(nameof(mcpExecutionBindingResolver));
        _mcpAgenticToolAdapter = mcpAgenticToolAdapter ?? throw new ArgumentNullException(nameof(mcpAgenticToolAdapter));
        _mcpWorkspaceSessionFactory = mcpWorkspaceSessionFactory ?? throw new ArgumentNullException(nameof(mcpWorkspaceSessionFactory));
        _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));
        _externalProviderRegistry = externalProviderRegistry ?? throw new ArgumentNullException(nameof(externalProviderRegistry));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<string> SpawnAsync(SubAgentSpawnRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Task) || !SubAgentSpawnPolicy.HasExactlyOneBinding(request))
        {
            return ReasonInvalidArguments;
        }

        // Runtime depth guard (defense-in-depth behind the structural cap): a spawned child runs at Depth ≥ 1 and its
        // tool set already omits spawn_subagent, so it can never reach here — but if a misconfiguration ever offered it
        // the tool, this rejects rather than recursing. A missing context defaults SAFE (rejected below as no fan-out).
        var context = SpawnContext.Current;
        if (context is { Depth: >= 1 })
        {
            return ReasonDepthExceeded;
        }

        // Trust guard at the SERVICE seam, behind the offer gate that already withholds spawn_subagent from a parent
        // outside the trust boundary. Both exist because the two seams fail differently: an agent profile's
        // AllowedToolNames, a saved definition pinned to another model, or a future caller reaching this service
        // directly would all bypass the offer. Delegation is an egress decision — the child can be bound to a
        // node-local model that reads the workspace and knowledge base, and its answer returns into the parent's
        // transcript — so a parent that may not read that data itself may not obtain it through a child.
        if (await IsOutsideTrustBoundaryAsync(context?.RootModelId, ct).ConfigureAwait(false))
        {
            return ReasonParentOutsideTrustBoundary;
        }

        // Per-root fan-out cap: the lease releases its slot on dispose; the using-declaration releases it on every
        // return path below. A missing ambient context means "no spawn context was seeded" → reject conservatively.
        using var fanOutLease = context?.TryEnterFanOut();
        if (context is null || fanOutLease is null)
        {
            return ReasonFanOutExceeded;
        }

        // A sub-agent always runs a chat/tool loop, so it competes for a Chat-role process.
        const ModelRole role = ModelRole.Chat;

        var binding = await ResolveBindingAsync(request, ct).ConfigureAwait(false);
        if (binding is null)
        {
            return ReasonSubAgentUnresolved;
        }

        var decision = await _capacityService.DecideAsync(binding.ModelName, role, ct).ConfigureAwait(false);

        return decision.Verdict switch
        {
            CapacityVerdict.Allow => await RunAllowAsync(binding, decision, context, request.Task, ct).ConfigureAwait(false),
            CapacityVerdict.QueueSameModel => await RunQueuedAsync(binding, context, role, request.Task, ct).ConfigureAwait(false),
            _ => decision.Reason
        };
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The whole-turn deadline lives HERE, at the one boundary both inbound MCP front doors cross — the synchronous
    ///     <c>run_agent</c> tool and the detached <c>start_agent_run</c> executor — so an inbound run is bounded exactly
    ///     once, by the SAME operator knob that bounds a local chat send/regenerate and a scheduled run. Wrapping only
    ///     one caller left the other with no whole-turn bound at all. The setting is read per execution, so a Save
    ///     applies to the next run without a node restart.
    /// </remarks>
    public async Task<SpawnOutcome> SpawnForMcpAsync(McpExecutionBindingRequest request,
        string task,
        string? expectedBindingFingerprint,
        CancellationToken ct,
        Guid? workspaceId = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var nodeSettings = await _nodeSettingsStore.LoadAsync(ct).ConfigureAwait(false);

        // Linked, not replaced: the caller's token still wins (operator cancel, dispatcher watchdog, host shutdown) and
        // its durable stop marker still chooses the terminal outcome. This only adds the missing whole-turn deadline.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(nodeSettings.MaxMessageRequestTimeoutSeconds));

        try
        {
            return await SpawnForMcpCoreAsync(request, task, expectedBindingFingerprint, workspaceId, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Only OUR deadline fired (both halves are required: an unrelated inner token must not be reported as this
            // node's timeout, and the caller's own cancellation must escape). A typed outcome instead of an escaping
            // cancellation is what lets get_agent_run report a distinguishable failure_code the caller can act on.
            return SpawnOutcome.Failed(McpExecutionFailureCodes.TimedOut,
                "The run exceeded the node's maximum message request timeout.");
        }
    }

    private async Task<SpawnOutcome> SpawnForMcpCoreAsync(McpExecutionBindingRequest request,
        string task,
        string? expectedBindingFingerprint,
        Guid? workspaceId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return SpawnOutcome.Rejected(McpExecutionFailureCodes.InvalidRequest, "Cannot run: provide a non-empty task.");
        }

        var context = SpawnContext.Current;
        if (context is { Depth: >= 1 })
        {
            return SpawnOutcome.Rejected(McpExecutionFailureCodes.CapacityDeclined, ReasonDepthExceeded);
        }

        var resolution = await _mcpExecutionBindingResolver.ResolveAsync(request, ct).ConfigureAwait(false);
        if (resolution.Binding is not { } mcpBinding)
        {
            return SpawnOutcome.Rejected(resolution.FailureCode ?? McpExecutionFailureCodes.InternalFailure, resolution.DisplayMessage);
        }

        if (expectedBindingFingerprint is not null
            && !string.Equals(expectedBindingFingerprint, mcpBinding.BindingFingerprint, StringComparison.Ordinal))
        {
            return SpawnOutcome.Rejected(McpExecutionFailureCodes.AgentConfigChanged,
                "Cannot run: the accepted agent execution configuration has changed.");
        }

        using var fanOutLease = context?.TryEnterFanOut();
        if (context is null || fanOutLease is null)
        {
            return SpawnOutcome.Rejected(McpExecutionFailureCodes.CapacityDeclined, ReasonFanOutExceeded);
        }

        var binding = await TryCreateResolvedMcpBindingAsync(mcpBinding, request, ct).ConfigureAwait(false);
        if (binding is null)
        {
            return SpawnOutcome.Rejected(McpExecutionFailureCodes.AgentConfigChanged,
                "Cannot run: the accepted agent capability configuration is not valid.");
        }

        var isWorkspaceCoder = McpExecutionBindingPolicy.IsExactReadOnlyWorkspaceCoder(mcpBinding);
        if ((isWorkspaceCoder && workspaceId is null) || (!isWorkspaceCoder && workspaceId is not null))
        {
            return SpawnOutcome.Rejected(McpExecutionFailureCodes.WorkspaceNotAuthorized,
                "Cannot run: the selected workspace is not authorized.");
        }

        const ModelRole role = ModelRole.Chat;
        var decision = await _capacityService.DecideAsync(binding.ModelName, role, ct).ConfigureAwait(false);
        return decision.Verdict switch
        {
            CapacityVerdict.Allow => await RunAllowedMcpAsync(binding, decision, context, task, workspaceId, ct).ConfigureAwait(false),
            CapacityVerdict.QueueSameModel => await RunQueuedMcpAsync(binding, context, role, task, workspaceId, ct).ConfigureAwait(false),
            _ => SpawnOutcome.Rejected(McpExecutionFailureCodes.CapacityDeclined, decision.Reason)
        };
    }

    private async Task<SpawnOutcome> RunAllowedMcpAsync(ResolvedBinding binding,
        CapacityDecision decision,
        SpawnContext context,
        string task,
        Guid? workspaceId,
        CancellationToken ct)
    {
        if (decision.Reservation is null && !context.TryConsumeCloudSpawn())
        {
            return SpawnOutcome.Rejected(McpExecutionFailureCodes.CapacityDeclined, ReasonCloudCapExceeded);
        }

        try
        {
            var workspace = await OpenWorkspaceAsync(workspaceId, ct).ConfigureAwait(false);
            if (workspace.Failure is { } failure)
            {
                return failure;
            }

            using var session = workspace.Session;
            using var ambient = session?.EnterAmbientScope();
            var content = await RunSubAgentAsync(binding, context, task, ct).ConfigureAwait(false);
            return SpawnOutcome.Success(content);
        }
        finally
        {
            decision.Reservation?.Dispose();
        }
    }

    private async Task<SpawnOutcome> RunQueuedMcpAsync(ResolvedBinding binding,
        SpawnContext context,
        ModelRole role,
        string task,
        Guid? workspaceId,
        CancellationToken ct)
    {
        var workspace = await OpenWorkspaceAsync(workspaceId, ct).ConfigureAwait(false);
        if (workspace.Failure is { } failure)
        {
            return failure;
        }

        using var session = workspace.Session;
        using var ambient = session?.EnterAmbientScope();
        var timedOut = false;
        var content = await _spawnSerializer.RunSerializedAsync(binding.ModelName,
            role,
            TimeSpan.FromSeconds(_options.QueueWaitSeconds),
            innerCt => RunSubAgentAsync(binding, context, task, innerCt),
            () =>
            {
                timedOut = true;
                return string.Empty;
            },
            ct).ConfigureAwait(false);
        return timedOut
            ? SpawnOutcome.Rejected(McpExecutionFailureCodes.CapacityDeclined, ReasonQueueBusy)
            : SpawnOutcome.Success(content);
    }

    private async Task<ResolvedBinding?> TryCreateResolvedMcpBindingAsync(McpExecutionBinding binding,
        McpExecutionBindingRequest request,
        CancellationToken cancellationToken)
    {
        IList<AITool>? tools;
        if (request.InboundContext.IsAgentic)
        {
            var agentic = await ResolveAgenticMcpToolsAsync(binding.AllowedTools, request, cancellationToken).ConfigureAwait(false);
            if (!agentic.Success)
            {
                return null;
            }

            tools = agentic.Tools;
        }
        else if (!TryResolveDelegateMcpTools(binding.AllowedTools, out tools))
        {
            return null;
        }

        var reasoning = binding.AgentDefinitionId is null
            ? null
            : new ChildReasoning(binding.ReasoningEffort, binding.SupportsThinking, binding.ReasoningBudgetEnforceable);
        return new ResolvedBinding(binding.ModelId, binding.Instructions, tools, reasoning, Skills: null);
    }

    private bool TryResolveDelegateMcpTools(IReadOnlyList<AllowedToolDto> allowedTools, out IList<AITool>? tools)
    {
        if (allowedTools.Count == 0)
        {
            tools = null;
            return true;
        }

        var safeOffer = allowedTools
                        .Where(static tool => tool.Category == ToolCategory.ReadLocal
                                              && !tool.RequiresApproval
                                              && (string.Equals(tool.Name, "list_files", StringComparison.Ordinal)
                                                  || string.Equals(tool.Name, "read_file", StringComparison.Ordinal)
                                                  || string.Equals(tool.Name, "search_text", StringComparison.Ordinal)))
                        .ToArray();
        if (safeOffer.Length != 3
            || !SubAgentSpawnPolicy.HasExactCoderToolNames(safeOffer.Select(static tool => tool.Name))
            || safeOffer.Length != allowedTools.Count)
        {
            tools = null;
            return false;
        }

        var executables = InvocationToolResolver.Resolve(SubAgentSpawnPolicy.ToOfferPlaceholders(safeOffer),
            _toolRegistry,
            _clientLocalToolRegistry,
            _mcpToolRegistry,
            _logger);
        if (executables.Count != 3
            || executables.Any(static tool => tool is ApprovalRequiredAIFunction)
            || !SubAgentSpawnPolicy.HasExactCoderToolNames(executables.Select(static tool => tool.Name)))
        {
            tools = null;
            return false;
        }

        tools = executables.ToList();
        return true;
    }

    private async Task<(bool Success, IList<AITool>? Tools)> ResolveAgenticMcpToolsAsync(IReadOnlyList<AllowedToolDto> allowedTools,
        McpExecutionBindingRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.InboundContext.IsAgentic
            || !McpInboundExecutionContext.IsBoundedPrefix(request.InboundContext.KeyPrefix)
            || request.ExecutionRequestId == Guid.Empty)
        {
            return (false, null);
        }

        if (allowedTools.Count == 0)
        {
            return (true, null);
        }

        if (allowedTools.Any(static descriptor => string.IsNullOrWhiteSpace(descriptor.Name))
            || allowedTools.Select(static descriptor => descriptor.Name).Distinct(StringComparer.Ordinal).Count() != allowedTools.Count)
        {
            return (false, null);
        }

        var executables = await InvocationToolResolver.ResolveAsync(SubAgentSpawnPolicy.ToOfferPlaceholders(allowedTools),
            _toolRegistry,
            _clientLocalToolRegistry,
            _mcpToolRegistry,
            _customToolCatalog,
            _logger,
            cancellationToken).ConfigureAwait(false);
        if (executables.Count != allowedTools.Count
            || executables.Select(static executable => executable.Name).Distinct(StringComparer.Ordinal).Count() != allowedTools.Count)
        {
            return (false, null);
        }

        var descriptors = allowedTools.ToDictionary(static descriptor => descriptor.Name, StringComparer.Ordinal);
        var adapted = new List<AITool>(executables.Count);
        foreach (var executable in executables)
        {
            if (!descriptors.TryGetValue(executable.Name, out var descriptor))
            {
                return (false, null);
            }

            if (executable is ApprovalRequiredAIFunction approvalRequired)
            {
                adapted.Add(_mcpAgenticToolAdapter.Adapt(approvalRequired,
                    descriptor.Category,
                    request.InboundContext,
                    request.ExecutionRequestId));
            }
            else
            {
                if (descriptor.RequiresApproval)
                {
                    return (false, null);
                }

                adapted.Add(executable);
            }
        }

        return (true, adapted);
    }

    // Allow: a cloud spawn consumes a cloud-budget unit (DoS-of-wallet cap); a local Allow carries a ledger reservation
    // that must be released when the child exits. The reservation is null for a cloud Allow, so disposal is a no-op there.
    /// <summary>
    ///     Whether prompts for <paramref name="modelId" /> leave the node — the same formula
    ///     <c>LocalToolOfferProvider.IsOutsideTrustBoundary</c> applies, spelled asynchronously because this seam has an
    ///     async boundary and can therefore resolve the registry rather than settle for its cached generation.
    /// </summary>
    /// <remarks>
    ///     A <see langword="null" /> id means the seeding caller named no model (an inbound MCP run resolves its own
    ///     binding downstream), which is not a claim that the parent is remote — so it does not refuse.
    /// </remarks>
    private async Task<bool> IsOutsideTrustBoundaryAsync(string? modelId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        return CodexModelCatalog.IsCodexModel(modelId)
               || await _modelTrustResolver.ResolveAsync(modelId, ct).ConfigureAwait(false) != ModelTrustLocality.Local;
    }

    private async Task<string> RunAllowAsync(ResolvedBinding binding,
        CapacityDecision decision,
        SpawnContext context,
        string task,
        CancellationToken ct)
    {
        if (decision.Reservation is null && !context.TryConsumeCloudSpawn())
        {
            // A null reservation on Allow means the cloud bypass admitted it; gate it on the cloud-spawn budget.
            return ReasonCloudCapExceeded;
        }

        try
        {
            return await RunSubAgentAsync(binding, context, task, ct).ConfigureAwait(false);
        }
        finally
        {
            decision.Reservation?.Dispose();
        }
    }

    // QueueSameModel: serialize against the one running process (no second load) with a bounded wait. The byte ledger
    // is not touched — the model is already resident — so there is no reservation to release here.
    private async Task<string> RunQueuedAsync(ResolvedBinding binding, SpawnContext context, ModelRole role, string task, CancellationToken ct)
    {
        return await _spawnSerializer.RunSerializedAsync(binding.ModelName,
            role,
            TimeSpan.FromSeconds(_options.QueueWaitSeconds),
            innerCt => RunSubAgentAsync(binding, context, task, innerCt),
            static () => ReasonQueueBusy,
            ct).ConfigureAwait(false);
    }

    // Builds the bound sub-agent (mirrors OrchestrationAgentFactory.BuildAgent's ChatClientAgent ctor) with the curated
    // tool set the binding resolved (spawn_subagent already filtered out), then runs it as an AIFunction inside a child
    // SpawnContext scope (Depth+1) so the runtime depth guard holds for any nested call. The outer ct flows into the
    // inner run (verified spike).
    private async Task<string> RunSubAgentAsync(ResolvedBinding binding, SpawnContext context, string task, CancellationToken ct)
    {
        // Pin the child's OWN binding, exactly as InvocationRunner pins the parent turn's. The parent's pin is keyed by
        // the parent model, so without this an external child's sends found no pin and fell through to the transport's
        // weaker unpinned check — while the child is running with a tool set authorized against the declaration read
        // here. Resolved first and scoped HERE: an AsyncLocal seeded inside the async helper would not survive its
        // return. The child runs inside this frame's flow, so the scope reaches it; pins stack, so the parent's lives.
        var childPins = await ExternalProviderInvocationPin.ResolveAsync(_externalProviderRegistry, binding.ModelName, ct).ConfigureAwait(false);
        using var childBindingPin = ExternalProviderBindingPinScope.Begin(childPins);

        // The child MUST run on its bound model: RuntimeChatClient routes the shared IChatClient to a provider PER SEND
        // off ChatOptions.ModelId (mirrors InvocationAgentFactory). Without it the inner run falls back to the node
        // default provider/model — e.g. a llama.cpp GGUF sub-agent would be sent to Ollama and fail. Instructions + the
        // curated tools also ride ChatOptions (the ChatClientAgentOptions ctor path; MAF lands the positional args there
        // too). AsAIFunction invokes the agent with no per-run options, so these construction-time defaults apply.
        var chatOptions = new ChatOptions
        {
            ModelId = binding.ModelName,
            Instructions = binding.Instructions,
            Tools = binding.Tools
        };

        // Profile-bound child: bake the resolved reasoning effort into the construction-time ChatOptions, gated on the
        // child model's own thinking capability — the SAME contract the orchestration-participant path uses
        // (ParticipantReasoningOptions), because an agent-as-tool never receives per-run RunOptions (AsAIFunction invokes
        // with no options), so RunOptions.ChatOptions — the single-agent path's reasoning channel — never reaches it.
        // Null for a model-id-only spawn, which stays as-is (no reasoning field on the wire).
        if (binding.Reasoning is { } reasoning)
        {
            chatOptions.AdditionalProperties = ParticipantReasoningOptions.Build(reasoning.ReasoningEffort,
                reasoning.SupportsThinking,
                reasoning.ReasoningBudgetEnforceable,
                _logger,
                binding.ModelName);
        }

        var agentOptions = new ChatClientAgentOptions
        {
            Name = SubAgentName,
            Description = SubAgentDescription,
            ChatOptions = chatOptions
        };

        // Attach the resolved skills as a MAF progressive-disclosure provider, mirroring InvocationAgentFactory's skills
        // path so a saved sub-agent offers its own skills on demand. Empty/null leaves the construction byte-identical to
        // a no-skills child (no context provider attached).
        AttachSkillsProvider(agentOptions, binding.Skills, _logger);

        var agent = new ChatClientAgent(_chatClient, agentOptions, _loggerFactory);

        var function = agent.AsAIFunction();

        // AsAIFunction forwards the outer ct into the inner run, so a cancelled parent cancels the child and an OCE
        // propagates up to the caller's loop (no swallow, no linked CTS needed — verified by the ct-propagation spike).
        // BeginChildScope pushes Depth+1 for the inner run WITHOUT re-seeding a root, then restores the parent context.
        var arguments = new AIFunctionArguments(StringComparer.Ordinal)
        {
            [InnerAgentInputKey] = task
        };

        using (context.BeginChildScope())
        {
            var result = await function.InvokeAsync(arguments, ct).ConfigureAwait(false);
            return result?.ToString() ?? string.Empty;
        }
    }
}
