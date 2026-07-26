namespace XE_Local_AI_Engine.Client.Services.Capacity;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Instructions;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Capacity.Tools;
using XE_Local_AI_Engine.Client.Services.Chat;
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
internal sealed class SubAgentSpawnService : ISubAgentSpawnService
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

    private readonly IAgentDefinitionResolver _agentDefinitionResolver;
    private readonly ICapacityService _capacityService;
    private readonly IChatClient _chatClient;
    private readonly IClientLocalToolRegistry _clientLocalToolRegistry;
    private readonly IAgentDefinitionStore _definitionStore;
    private readonly IAgentInstructionProvider _instructionProvider;
    private readonly ILogger<SubAgentSpawnService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IMcpToolRegistry _mcpToolRegistry;
    private readonly IModelCapabilityResolver _modelCapabilityResolver;
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
        IModelCapabilityResolver modelCapabilityResolver,
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
        _modelCapabilityResolver = modelCapabilityResolver ?? throw new ArgumentNullException(nameof(modelCapabilityResolver));
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
            chatOptions.AdditionalProperties = ParticipantReasoningOptions.Build(reasoning.ReasoningEffort, reasoning.SupportsThinking);
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
        AttachSkillsProvider(agentOptions, binding.Skills);

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

        // Resolve the FULL runtime for the bound child in ONE pass — the same ResolvedAgentRuntime a direct agent send
        // consumes — so the child inherits the resolved system prompt (scaffold + persona + injected playbook memory),
        // reasoning effort, and skills as one unit, not just its curated tool set. Reading only AllowedTools here used to
        // let a saved sub-agent silently run on raw definition.Instructions with no scaffold, reasoning, or
        // skills — LESS grounding than the anonymous model-id-only path, which already composes the base scaffold.
        var resolved = await _agentDefinitionResolver
                             .ResolveAsync(definition.Id, definition.ModelProfile, cancellationToken: ct)
                             .ConfigureAwait(false);
        if (resolved is null)
        {
            // TOCTOU: the definition was deleted between the fetch above and this resolve. Reject with the sanitized
            // unresolved reason rather than degrade to raw instructions (the very bypass this fix closes).
            return null;
        }

        // The profile's OWN curated tool set: offer ∩ AllowedToolNames (already capability-gated by the resolver),
        // bridged to executables, then UNCONDITIONALLY strip spawn_subagent so the child can never spawn (the structural
        // depth cap), regardless of what its AllowedToolNames lists.
        var tools = CurateChildTools(resolved.AllowedTools);

        // The child model's OWN thinking capability gates the reasoning field, exactly as the direct path
        // (resolution.SupportsThinking) and the orchestration-participant path (participant.SupportsThinking) gate
        // theirs: a non-thinking Ollama model 400s on think:true/level, so ParticipantReasoningOptions omits the field
        // for it. Cache-first; no probe on a cache hit.
        // The child's knowledge-tool locality gate is applied inside AgentDefinitionResolver above (it classifies the
        // pinned effective model, which for a spawned child IS definition.ModelProfile), so only the thinking bit is
        // taken here; the locality element is ignored.
        var (supportsThinking, _, _) = await _modelCapabilityResolver
                                             .ResolveAsync(definition.ModelProfile, ct)
                                             .ConfigureAwait(false);

        return new ResolvedBinding(definition.ModelProfile!,
            resolved.ResolvedSystemPrompt,
            tools,
            new ChildReasoning(resolved.ReasoningEffort, supportsThinking),
            resolved.Skills);
    }

    // Bridges the resolver's curated AllowedTools (capability-gated, profile-pool offer ∩ AllowedToolNames) to
    // executables via the shared InvocationToolResolver, then filters spawn_subagent out (the structural depth cap).
    private IList<AITool>? CurateChildTools(IReadOnlyList<AllowedToolDto> allowedTools)
    {
        if (allowedTools.Count == 0)
        {
            return null;
        }

        var offeredExecutables = InvocationToolResolver.Resolve(ToOfferPlaceholders(allowedTools),
            _toolRegistry,
            _clientLocalToolRegistry,
            _mcpToolRegistry,
            _logger);

        // Two unconditional strips, both structural — a curated child tool is never one of these:
        //   (1) DEPTH CAP: spawn_subagent, so a child can never spawn (mirrored by the runtime Depth guard).
        //   (2) NO HITL ROUTE: any ApprovalRequiredAIFunction. A child runs as an agent-as-tool via
        //       AsAIFunction, which invokes with no per-run options and no approval round-trip — an approval-gated tool
        //       would surface a ToolApprovalRequestContent the child can never answer, silently failing every call to it.
        //       The tools are DROPPED (and warned, naming them), never unwrapped to auto-execute — unwrapping would
        //       bypass the approval control the offer/registry/MCP policy asserted.
        var curated = new List<AITool>(offeredExecutables.Count);
        List<string>? droppedApprovalTools = null;
        foreach (var tool in offeredExecutables)
        {
            if (string.Equals(tool.Name, SpawnSubAgentToolDefinition.ToolName, StringComparison.Ordinal))
            {
                continue;
            }

            if (tool is ApprovalRequiredAIFunction)
            {
                (droppedApprovalTools ??= []).Add(tool.Name);
                continue;
            }

            curated.Add(tool);
        }

        if (droppedApprovalTools is { Count: > 0 })
        {
            _logger.LogWarning("Dropped {DroppedCount} approval-required tool(s) from a sub-agent child ({DroppedTools}); a spawned child has no human-in-the-loop approval route.",
                droppedApprovalTools.Count,
                string.Join(", ", droppedApprovalTools));
        }

        return curated.Count > 0 ? curated : null;
    }

    // Builds a MAF AgentSkillsProvider from the resolved node skills and attaches it to the child agent's options,
    // mirroring InvocationAgentFactory.BuildAgent's skills path (name + description + body-as-instructions; no
    // scripts/resources in v1). Empty/null is a no-op so a no-skills child stays byte-identical.
    private static void AttachSkillsProvider(ChatClientAgentOptions agentOptions, IReadOnlyList<ResolvedSkill>? skills)
    {
        if (skills is not { Count: > 0 } resolvedSkills)
        {
            return;
        }

        // MAAI001: Agent Skills (AgentSkillsProvider/AgentInlineSkill) shipped [Experimental] in Microsoft.Agents.AI
        // in 1.8.0. The scoped MAAI001 suppression remains at the pinned 1.15.0 until explicit graduation evidence is
        // available. Reached only when the child agent has assigned skills, the
        // same scoped suppression InvocationAgentFactory uses.
#pragma warning disable MAAI001
        var inlineSkills = new AgentInlineSkill[resolvedSkills.Count];
        for (var index = 0; index < resolvedSkills.Count; index++)
        {
            var skill = resolvedSkills[index];
            inlineSkills[index] = new AgentInlineSkill(skill.Name, skill.Description, skill.Body);
        }

#pragma warning disable CA2000 // Ownership transfers to the ChatClientAgent via AIContextProviders; the agent disposes its context providers with itself.
        agentOptions.AIContextProviders = [new AgentSkillsProvider(inlineSkills)];
#pragma warning restore CA2000
#pragma warning restore MAAI001
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
               .Select(static tool => InvocationToolBridge.CreateOfferPlaceholder(tool.Name, tool.RequiresApproval))
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

    // The child's fully-resolved run inputs. Instructions is the resolved system prompt for a profile-bound child (the
    // scaffold + persona + injected playbook memory), or the raw request instructions for a model-id-only child.
    // Reasoning + Skills are populated only for a profile-bound child (null for model-id-only, keeping that path as-is).
    private sealed record ResolvedBinding(
        string ModelName,
        string Instructions,
        IList<AITool>? Tools,
        ChildReasoning? Reasoning = null,
        IReadOnlyList<ResolvedSkill>? Skills = null);

    // The child's reasoning inputs: the resolved effort plus the child model's OWN thinking capability, which together
    // drive ParticipantReasoningOptions.Build exactly as the orchestration-participant path does.
    private sealed record ChildReasoning(string? ReasoningEffort, bool SupportsThinking);
}
