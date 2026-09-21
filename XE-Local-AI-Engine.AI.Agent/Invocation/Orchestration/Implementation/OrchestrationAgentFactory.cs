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
    // ProviderCallBudgetChatClient reads — the per-participant effective context window rides it.
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

        // One agent per participant over the production-decorated IChatClient, whose FICC services only the agent's own
        // tools and lets the bodyless handoff_to_* declarations reach the executor unserviced (pinned by a named test).
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

        // A handoff workflow drives its participants itself, so the outer runner's RunOptions.ChatOptions never reaches
        // them: model id and reasoning are baked in at CONSTRUCTION through ChatClientAgentOptions.ChatOptions.
        var additionalProperties = ParticipantReasoningOptions.Build(participant.ReasoningEffort,
            participant.SupportsThinking,
            participant.ReasoningBudgetEnforceable,
            _logger,
            participant.ModelId);

        // This participant's launched window as num_ctx, so ProviderCallBudgetChatClient sizes IT against its own model
        // rather than the shared default; the ContainsKey guard leaves any per-send override in place.
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
            // No explicit edges means a fully-connected mesh: registering the non-initial participants with no handoff
            // edges auto-wires every agent to every other (CreateHandoffBuilderWith already registered triage).
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
