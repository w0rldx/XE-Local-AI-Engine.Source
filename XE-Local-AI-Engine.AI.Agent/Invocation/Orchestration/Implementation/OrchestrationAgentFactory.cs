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
    private readonly IChatClient _chatClient;
    private readonly IClientLocalToolRegistry _clientLocalToolRegistry;
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
        IMcpToolRegistry mcpToolRegistry)
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
        // them flow through unserviced. (The spike notes "do NOT pre-wrap" for an EXTERNAL FICC that would try to
        // invoke handoff_to_*; the pre-decorated pipeline is safe because the same FICC services only the agent's own
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

            var agent = BuildAgent(participant);
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
        // conversation. Mirrors the §1.8 spike — without this the run idles forever.
        _ = await streamingRun
                  .TrySendMessageAsync(new TurnToken(definition.EmitStreamingUpdates))
                  .ConfigureAwait(false);

        return new OrchestrationRunSession(streamingRun,
            participantsByAgentId,
            TimeSpan.FromSeconds(_options.IdleTimeoutSeconds),
            _logger);
    }

    private AIAgent BuildAgent(OrchestrationParticipant participant)
    {
        var tools = InvocationToolResolver.Resolve(participant.Tools, _toolRegistry, _clientLocalToolRegistry, _mcpToolRegistry, _logger);

        // ChatClientAgent ctor order is (chatClient, instructions, name, description, tools, loggerFactory, sp) —
        // instructions precede name. Named arguments keep that unambiguous.
        return new ChatClientAgent(_chatClient,
            instructions: participant.Instructions,
            name: participant.Name,
            description: participant.Description,
            tools: tools,
            loggerFactory: _loggerFactory,
            services: _serviceProvider);
    }

    private static Workflow BuildWorkflow(OrchestrationAgentDefinition definition,
        AIAgent triageAgent,
        IReadOnlyDictionary<string, AIAgent> agentsByKey)
    {
#pragma warning disable MAAIW001 // CreateHandoffBuilderWith is [Experimental]; adopted deliberately (loop plan §1.8).
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

        _ = builder.EmitAgentResponseEvents(true);
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
