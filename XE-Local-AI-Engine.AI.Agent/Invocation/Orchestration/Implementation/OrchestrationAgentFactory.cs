namespace XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration.Implementation;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;

internal sealed class OrchestrationAgentFactory : IOrchestrationAgentFactory
{
    // The Ollama num_ctx option key, byte-identical to SamplingOptionKeys.NumCtx and the key
    // ProviderCallBudgetChatClient reads — the per-participant effective context window rides it (ORC-07).
    private const string NumCtxKey = "num_ctx";

    private readonly IChatClient _chatClient;
    private readonly IClientLocalToolRegistry _clientLocalToolRegistry;
    private readonly ICustomToolCatalog _customToolCatalog;
    private readonly ILogger<OrchestrationAgentFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IMcpToolRegistry _mcpToolRegistry;
    private readonly OrchestrationAgentOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly IAgentToolRegistry _toolRegistry;

    public OrchestrationAgentFactory(IChatClient chatClient,
        IOptions<OrchestrationAgentOptions> options,
        ILogger<OrchestrationAgentFactory> logger,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        IAgentToolRegistry toolRegistry,
        IClientLocalToolRegistry clientLocalToolRegistry,
        IMcpToolRegistry mcpToolRegistry,
        ICustomToolCatalog customToolCatalog)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _options = options.Value;
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _clientLocalToolRegistry = clientLocalToolRegistry ?? throw new ArgumentNullException(nameof(clientLocalToolRegistry));
        _mcpToolRegistry = mcpToolRegistry ?? throw new ArgumentNullException(nameof(mcpToolRegistry));
        _customToolCatalog = customToolCatalog ?? throw new ArgumentNullException(nameof(customToolCatalog));
    }

    public async Task<IOrchestrationRunSession> CreateAsync(OrchestrationAgentDefinition definition,
        IReadOnlyList<ChatMessage> seed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(seed);
        cancellationToken.ThrowIfCancellationRequested();

        if (definition.Participants.Count == 0)
        {
            throw new ArgumentException("An orchestration must declare at least one participant.", nameof(definition));
        }

        // Build one agent per participant. Each agent receives the production-decorated IChatClient (the DI pipeline
        // is already FunctionInvokingChatClient-wrapped) plus its own resolved tools. ChatClientAgent's ctor detects
        // the existing FICC and sets the agent's own tools as AdditionalTools, while the handoff builder injects the
        // bodyless handoff_to_* declarations the executor routes — FICC has no implementation for those so it lets
        // them flow through unserviced. An external FICC that tried to invoke handoff_to_* would be unsafe, but the
        // pre-decorated pipeline is safe because the same FICC services only the agent's own
        // tools — proven by CreateAsync_ApprovalAcrossHandoff_OverProductionDecoratedClient_StillSurfacesAndExecutes.)
        // The agent's Name/Description drive handoff routing (the target's Description is the routing reason).
        var agentsByKey = new Dictionary<string, AIAgent>(StringComparer.Ordinal);
        var participantsByAgentId = new Dictionary<string, OrchestrationParticipant>(StringComparer.Ordinal);
        foreach (var participant in definition.Participants)
        {
            if (agentsByKey.ContainsKey(participant.Key))
            {
                throw new ArgumentException($"Duplicate participant key '{participant.Key}' in orchestration.", nameof(definition));
            }

            var agent = await BuildAgentAsync(participant, cancellationToken).ConfigureAwait(false);
            agentsByKey[participant.Key] = agent;
            participantsByAgentId[agent.Id] = participant;
        }

        if (!agentsByKey.TryGetValue(definition.Triage.Key, out var triageAgent))
        {
            throw new ArgumentException($"Triage key '{definition.Triage.Key}' is not among the orchestration participants.", nameof(definition));
        }

        var workflow = BuildWorkflow(definition, triageAgent, agentsByKey);

        var runId = $"orchestration-{Guid.NewGuid():N}";
        var streamingRun = await InProcessExecution
                                 .RunStreamingAsync(workflow, seed.ToList(), runId, cancellationToken)
                                 .ConfigureAwait(false);

        // HandoffStart only ACCUMULATES the seed messages (AutoSendTurnToken=false); a TurnToken actually starts the
        // conversation. Without this turn token the run idles forever after accepting the seed messages.
        _ = await streamingRun
                  .TrySendMessageAsync(new TurnToken(definition.EmitStreamingUpdates))
                  .ConfigureAwait(false);

        return new OrchestrationRunSession(streamingRun,
            participantsByAgentId,
            TimeSpan.FromSeconds(_options.IdleTimeoutSeconds),
            _logger);
    }

    private async Task<AIAgent> BuildAgentAsync(OrchestrationParticipant participant, CancellationToken cancellationToken)
    {
        var tools = await InvocationToolResolver.ResolveAsync(participant.Tools,
            _toolRegistry,
            _clientLocalToolRegistry,
            _mcpToolRegistry,
            _customToolCatalog,
            _logger,
            cancellationToken).ConfigureAwait(false);

        // A handoff workflow drives its participant agents itself, so the outer runner's per-turn RunOptions.ChatOptions
        // (which carries model id + reasoning on the single-agent path) NEVER reaches them. The participant's resolved
        // model + reasoning must therefore be baked into the agent at CONSTRUCTION time, via ChatClientAgentOptions.
        // ChatOptions — the same channel the single-agent factory's skills path uses. ModelId routes the shared
        // (production-decorated) IChatClient to this participant's resolved model; the reasoning AdditionalProperties
        // mirror the single-agent think contract (see ParticipantReasoningOptions), gated on the participant's own
        // thinking capability. Instructions + the agent's own tools ride ChatOptions exactly as the positional ctor
        // moves them under the hood, so ChatClientAgent still detects the pre-decorated FICC and treats the agent tools
        // as AdditionalTools while handoff_to_* flows through (proven by the approval-across-handoff tests).
        var additionalProperties = ParticipantReasoningOptions.Build(participant.ReasoningEffort,
            participant.SupportsThinking,
            participant.ReasoningBudgetEnforceable,
            _logger,
            participant.ModelId);

        // ORC-07: carry this participant's launched effective context window as num_ctx so the innermost provider-round
        // budgeter (ProviderCallBudgetChatClient) sizes THIS participant against the window ITS model was launched with,
        // not the shared configured default — a participant pinned to a smaller-window model could otherwise be fed past
        // its real window. Mirrors the single-agent InvocationAgentFactory num_ctx write; the ContainsKey guard leaves
        // any per-send override (none on this workflow path today) in place.
        if (participant.EffectiveContextTokens is { } effectiveContext
            && effectiveContext > 0
            && !additionalProperties.ContainsKey(NumCtxKey))
        {
            additionalProperties[NumCtxKey] = effectiveContext;
        }

        var chatOptions = new ChatOptions
        {
            Instructions = participant.Instructions,
            Tools = tools,
            ModelId = participant.ModelId,
            AdditionalProperties = additionalProperties
        };

        return new ChatClientAgent(_chatClient,
            new ChatClientAgentOptions
            {
                Name = participant.Name,
                Description = participant.Description,
                ChatOptions = chatOptions
            },
            _loggerFactory,
            _serviceProvider);
    }

    private static Workflow BuildWorkflow(OrchestrationAgentDefinition definition,
        AIAgent triageAgent,
        IReadOnlyDictionary<string, AIAgent> agentsByKey)
    {
#pragma warning disable MAAIW001 // CreateHandoffBuilderWith is [Experimental]; adopted deliberately for handoff orchestration.
        var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(triageAgent);
#pragma warning restore MAAIW001

        if (definition.Edges.Count == 0)
        {
            // No explicit edges means a fully-connected mesh: registering the non-initial participants with no
            // handoff edges makes the builder auto-wire every agent to hand off to every other (the initial/triage
            // agent is already registered by CreateHandoffBuilderWith).
            var others = definition.Participants
                                   .Where(participant => !string.Equals(participant.Key, definition.Triage.Key, StringComparison.Ordinal))
                                   .Select(participant => agentsByKey[participant.Key]);
            _ = builder.AddParticipants(others);
        }
        else
        {
            foreach (var edge in definition.Edges)
            {
                if (!agentsByKey.TryGetValue(edge.FromKey, out var from))
                {
                    throw new ArgumentException($"Handoff edge references unknown source participant '{edge.FromKey}'.", nameof(definition));
                }

                if (!agentsByKey.TryGetValue(edge.ToKey, out var to))
                {
                    throw new ArgumentException($"Handoff edge references unknown target participant '{edge.ToKey}'.", nameof(definition));
                }

                _ = builder.WithHandoff(from, to, edge.Reason);
            }
        }

        _ = builder.EmitAgentResponseEvents();
        _ = builder.EmitAgentResponseUpdateEvents(definition.EmitStreamingUpdates);

        if (definition.MaxTurnsPerAgent > 0)
        {
            _ = builder.WithAutonomousMode(definition.MaxTurnsPerAgent);
        }

        if (definition.ReturnToPrevious)
        {
            _ = builder.EnableReturnToPrevious();
        }

        return builder.Build();
    }
}
