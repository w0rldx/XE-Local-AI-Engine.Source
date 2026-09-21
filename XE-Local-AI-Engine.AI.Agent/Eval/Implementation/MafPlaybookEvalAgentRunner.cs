namespace XE_Local_AI_Engine.AI.Agent.Eval.Implementation;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;

/// <summary>
///     Microsoft Agent Framework (MAF) implementation of <see cref="IPlaybookEvalAgentRunner" />: a
///     <see cref="ChatClientAgent" /> over the supplied node-local chat client, empty tool set, run threadless.
/// </summary>
/// <remarks>
///     Mirrors the verified worker loop's prompt assembly (<c>InvocationAgentFactory.BuildSeedMessages</c>) — the
///     agent carries NO instructions, and the system instructions are delivered exactly once as the leading
///     <see cref="ChatRole.System" /> seed message — so the eval reproduces the real loop's outbound prompt.
/// </remarks>
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
        string? reasoningEffort = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(systemInstructions);
        ArgumentNullException.ThrowIfNull(inputTurns);
        cancellationToken.ThrowIfCancellationRequested();

        // Empty tool set, instructions NULL (the seed system message below delivers them), caller-owned chatClient NOT
        // disposed here. Named arguments pin the ctor order verified at Microsoft.Agents.AI 1.20.0 against a bump.
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

        // Threadless: a null session runs without persisted state, per the Microsoft.Agents.AI API re-verified at the
        // pin. Zero temperature keeps the gate's pass/fail on the prompt, not decoding noise; the judge pins its own.
        var chatOptions = new ChatOptions
        {
            Temperature = 0f
        };

        // Translated through the SAME matrix both production paths use, so the eval sends what a real turn at that
        // effort would. Assigned only when supplied: a null effort leaves AdditionalProperties null, hence unchanged.
        if (reasoningEffort is not null)
        {
            chatOptions.AdditionalProperties = ParticipantReasoningOptions.Build(reasoningEffort, supportsThinking: true);
        }

        var runOptions = new ChatClientAgentRunOptions
        {
            ChatOptions = chatOptions
        };
        var response = await agent.RunAsync(seed, session: null, runOptions, cancellationToken).ConfigureAwait(false);

        return response.Text ?? string.Empty;
    }
}
