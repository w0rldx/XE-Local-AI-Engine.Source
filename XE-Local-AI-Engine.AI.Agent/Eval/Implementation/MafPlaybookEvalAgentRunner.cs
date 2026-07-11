namespace XE_Local_AI_Engine.AI.Agent.Eval.Implementation;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
///     Microsoft Agent Framework (MAF) implementation of <see cref="IPlaybookEvalAgentRunner" />. Builds a
///     <see cref="ChatClientAgent" /> over the supplied (node-local) chat client with an empty tool set and runs
///     it threadless — mirroring the verified worker loop's prompt assembly
///     (<c>InvocationAgentFactory.BuildSeedMessages</c>): the agent carries NO instructions and the system
///     instructions are delivered exactly once as the leading <see cref="ChatRole.System" /> seed message, so the
///     eval reproduces the real loop's outbound prompt.
/// </summary>
internal sealed class MafPlaybookEvalAgentRunner : IPlaybookEvalAgentRunner
{
    private const string AgentName = "playbook-eval";
    private const string AgentDescription = "Golden-conversation eval runner.";

    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;

    public MafPlaybookEvalAgentRunner(ILoggerFactory loggerFactory, IServiceProvider serviceProvider)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public async Task<string> RunAsync(IChatClient chatClient,
        string systemInstructions,
        IReadOnlyList<ChatMessage> inputTurns,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(systemInstructions);
        ArgumentNullException.ThrowIfNull(inputTurns);
        cancellationToken.ThrowIfCancellationRequested();

        // Empty tool set: the eval gate measures the injected prompt's effect, not tool behaviour, so the loop runs
        // with no executable tools (no real side effects, no approval pauses). The chatClient is owned by the caller
        // (a node-local client) and is intentionally NOT disposed here. Instructions are NULL on the agent — like the
        // production loop, they are delivered once by the seed system message below. The ctor argument order is
        // (chatClient, instructions, name, description, tools, loggerFactory, services) — verified against
        // Microsoft.Agents.AI 1.13.0; named arguments pin it.
        var agent = new ChatClientAgent(chatClient,
            instructions: null,
            name: AgentName,
            description: AgentDescription,
            tools: new List<AITool>(),
            loggerFactory: _loggerFactory,
            services: _serviceProvider);

        // Mirror InvocationAgentFactory.BuildSeedMessages: a leading System(instructions) message followed by the
        // input turns. The system instructions are delivered exactly once — via this seed message, not the ctor.
        List<ChatMessage> seed =
        [
            new(ChatRole.System, systemInstructions),
            .. inputTurns
        ];

        // Threadless run (no AgentSession, no run options): per the Microsoft.Agents.AI API (verified at 1.8.0; pinned
        // version is now 1.13.0, not re-verified) the second argument is the session and a null value runs without
        // persisted state.
        var response = await agent.RunAsync(seed, session: null, options: null, cancellationToken).ConfigureAwait(false);

        return response.Text ?? string.Empty;
    }
}
