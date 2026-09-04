namespace XE_Local_AI_Engine.AI.Agent.Eval.Implementation;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;

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
        string? reasoningEffort = null,
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
        // Microsoft.Agents.AI 1.15.0; named arguments pin it.
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

        // Threadless run (no AgentSession — the second argument's null value runs without persisted state, per the
        // Microsoft.Agents.AI API re-verified at the pinned 1.15.0). The generation IS
        // pinned via run options: Temperature=0 makes the sampled text deterministic so the eval gate's pass/fail
        // reflects the injected prompt, not decoding noise — the judge (DefaultPlaybookEvalJudge) already pins its
        // own Temperature=0 independently. ChatClientAgentRunOptions.ChatOptions is the same shape
        // InvocationAgentFactory uses to carry per-request ChatOptions through to the model.
        var chatOptions = new ChatOptions
        {
            Temperature = 0f
        };

        // An effort is translated through the SAME matrix both production paths use, so what the eval sends a model is
        // what a real turn at that effort would send. Assigned only when one was supplied: a null effort leaves
        // AdditionalProperties null, which is what keeps every existing caller's request byte-identical.
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
