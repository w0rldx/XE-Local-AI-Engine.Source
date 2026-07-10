namespace XE_Local_AI_Engine.Client.Services.Capacity;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Instructions;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Capacity.Tools;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Default <see cref="ISubAgentSpawnService" />. Resolves the sub-agent's model binding + curated tool set, enforces
///     the depth/fan-out/cloud-spawn caps, calls the capacity gate, and dispatches per verdict. An admitted spawn runs a
///     <see cref="ChatClientAgent" /> as an <see cref="AIFunction" /> over the SAME production-decorated
///     <see cref="IChatClient" /> (cloud/local routed per send by the runtime client).
///     <para>
///         Depth cap (≤ 2) is enforced two ways: (1) PRIMARY, STRUCTURAL — the child's tool set has
///         <c>spawn_subagent</c> filtered out UNCONDITIONALLY, so a child can never spawn; (2) defense-in-depth —
///         <see cref="SpawnContext.Depth" /> ≥ 1 short-circuits to a sanitized reject. A profile-bound child inherits its
///         own definition's curated tools (offer ∩ AllowedToolNames, minus <c>spawn_subagent</c>); a model-id-only child
///         (no profile, no AllowedToolNames) is tool-less. Every expected rejection is a sanitized string, not an
///         exception.
///     </para>
/// </summary>
internal sealed class SubAgentSpawnService : ISubAgentSpawnService
{
    // The inner agent-as-tool exposes a single "query" input parameter (verified against MAF 1.10.0
    // AIAgentExtensions.AsAIFunction; pinned version is now 1.13.0, not re-verified); the spawn task is passed under
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

    private readonly IAgentDefinitionResolver _agentDefinitionResolver;
    private readonly ICapacityService _capacityService;
    private readonly IChatClient _chatClient;
    private readonly IClientLocalToolRegistry _clientLocalToolRegistry;
    private readonly IAgentDefinitionStore _definitionStore;
    private readonly IAgentInstructionProvider _instructionProvider;
    private readonly ILogger<SubAgentSpawnService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IMcpToolRegistry _mcpToolRegistry;
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
        IChatClient chatClient,
        IOptions<SpawnOptions> options,
        IAgentInstructionProvider instructionProvider,
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
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _instructionProvider = instructionProvider ?? throw new ArgumentNullException(nameof(instructionProvider));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<string> SpawnAsync(SubAgentSpawnRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Task) || !HasExactlyOneBinding(request))
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

        // A sub-agent always runs a chat/tool loop, so it competes for a Chat-role process.
        const ModelRole role = ModelRole.Chat;

        var binding = await ResolveBindingAsync(request, ct).ConfigureAwait(false);
        if (binding is null)
        {
            return ReasonSubAgentUnresolved;
        }

        // Per-root fan-out cap: the lease releases its slot on dispose; the using-declaration releases it on every
        // return path below. A missing ambient context means "no spawn context was seeded" → reject conservatively.
        using var fanOutLease = context?.TryEnterFanOut();
        if (context is null || fanOutLease is null)
        {
            return ReasonFanOutExceeded;
        }

        var decision = await _capacityService.DecideAsync(binding.ModelName, role, ct).ConfigureAwait(false);

        return decision.Verdict switch
        {
            CapacityVerdict.Allow => await RunAllowAsync(binding, decision, context, request.Task, ct).ConfigureAwait(false),
            CapacityVerdict.QueueSameModel => await RunQueuedAsync(binding, context, role, request.Task, ct).ConfigureAwait(false),
            _ => decision.Reason
        };
    }

    // Allow: a cloud spawn consumes a cloud-budget unit (DoS-of-wallet cap); a local Allow carries a ledger reservation
    // that must be released when the child exits. The reservation is null for a cloud Allow, so disposal is a no-op there.
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
        // The child MUST run on its bound model: RuntimeChatClient routes the shared IChatClient to a provider PER SEND
        // off ChatOptions.ModelId (mirrors InvocationAgentFactory). Without it the inner run falls back to the node
        // default provider/model — e.g. a llama.cpp GGUF sub-agent would be sent to Ollama and fail. Instructions + the
        // curated tools also ride ChatOptions (the ChatClientAgentOptions ctor path; MAF lands the positional args there
        // too). AsAIFunction invokes the agent with no per-run options, so these construction-time defaults apply.
        var agent = new ChatClientAgent(_chatClient,
            new ChatClientAgentOptions
            {
                Name = SubAgentName,
                Description = SubAgentDescription,
                ChatOptions = new ChatOptions
                {
                    ModelId = binding.ModelName,
                    Instructions = binding.Instructions,
                    Tools = binding.Tools
                }
            },
            _loggerFactory);

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

    private async Task<ResolvedBinding?> ResolveBindingAsync(SubAgentSpawnRequest request, CancellationToken ct)
    {
        // model-id-only binding: no agent profile, so no AllowedToolNames to curate from → the child is tool-less.
        if (!string.IsNullOrWhiteSpace(request.ModelId))
        {
            var instructions = string.IsNullOrWhiteSpace(request.Instructions)
                ? BaseInstructionComposer.Compose(_instructionProvider.GetBaseScaffold(), DefaultSubAgentPersonaInstructions)
                : request.Instructions!;
            return new ResolvedBinding(request.ModelId!, instructions, Tools: null);
        }

        var definition = await ResolveDefinitionAsync(request.SubAgentKey!, ct).ConfigureAwait(false);
        if (definition is null || string.IsNullOrWhiteSpace(definition.ModelProfile))
        {
            return null;
        }

        // Resolve the sub-agent profile's OWN curated tool set through the same offer→resolve path a normal agent send
        // uses (offer ∩ AllowedToolNames, then executable resolution), then UNCONDITIONALLY strip spawn_subagent so the
        // child can never spawn (the structural depth cap), regardless of what its AllowedToolNames lists.
        var tools = await ResolveChildToolsAsync(definition.Id, definition.ModelProfile, ct).ConfigureAwait(false);
        return new ResolvedBinding(definition.ModelProfile!, definition.Instructions, tools);
    }

    // Projects the sub-agent definition's AllowedToolNames through the resolver (capability-gated, profile-pool offer),
    // bridges the offer DTOs to executables via the shared InvocationToolResolver, then filters spawn_subagent out.
    private async Task<IList<AITool>?> ResolveChildToolsAsync(Guid definitionId, string? modelProfile, CancellationToken ct)
    {
        var resolved = await _agentDefinitionResolver.ResolveAsync(definitionId, modelProfile, cancellationToken: ct).ConfigureAwait(false);
        if (resolved is null || resolved.AllowedTools.Count == 0)
        {
            return null;
        }

        var offeredExecutables = InvocationToolResolver.Resolve(ToOfferPlaceholders(resolved.AllowedTools),
            _toolRegistry,
            _clientLocalToolRegistry,
            _mcpToolRegistry,
            _logger);

        // STRUCTURAL DEPTH CAP: the child must never carry the spawn tool, whatever its profile lists.
        var curated = offeredExecutables
                      .Where(static tool => !string.Equals(tool.Name, SpawnSubAgentToolDefinition.ToolName, StringComparison.Ordinal))
                      .ToArray();

        return curated.Length > 0 ? curated : null;
    }

    // Converts the profile's offer DTOs into the placeholder AITools InvocationToolResolver matches by name (mirrors the
    // single-agent path's BuildInvocationTools: ApiSide → bridge, ClientLocal → name-only placeholder swapped for the
    // registry executable). A sub-agent never round-trips to a client, so an ApiSide offer is dropped here.
    private static IReadOnlyList<AITool> ToOfferPlaceholders(IReadOnlyList<AllowedToolDto> offered)
    {
        return
        [
            .. offered
               .Where(static tool => tool.Location == ToolLocation.ClientLocal)
               .Select(static tool => InvocationToolBridge.CreateOfferPlaceholder(tool.Name))
        ];
    }

    // Resolve a persisted definition by GUID id first, then fall back to a case-sensitive name match. A spawn naming an
    // unknown/unbound (no ModelProfile) definition is rejected upstream, never fabricated.
    private async Task<AgentDefinitionRecord?> ResolveDefinitionAsync(string key, CancellationToken ct)
    {
        if (Guid.TryParse(key, out var id))
        {
            return await _definitionStore.GetByIdAsync(id, ct).ConfigureAwait(false);
        }

        var all = await _definitionStore.ListAsync(ct).ConfigureAwait(false);
        var match = all.FirstOrDefault(record => string.Equals(record.Name, key, StringComparison.Ordinal));
        if (match is null)
        {
            _logger.LogWarning("Sub-agent spawn referenced an unknown definition key.");
        }

        return match;
    }

    private static bool HasExactlyOneBinding(SubAgentSpawnRequest request)
    {
        var hasKey = !string.IsNullOrWhiteSpace(request.SubAgentKey);
        var hasModel = !string.IsNullOrWhiteSpace(request.ModelId);
        return hasKey ^ hasModel;
    }

    private sealed record ResolvedBinding(string ModelName, string Instructions, IList<AITool>? Tools);
}
