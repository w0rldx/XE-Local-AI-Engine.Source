namespace XE_Local_AI_Engine.Cli.Commands;

using System.CommandLine;
using XE_Local_AI_Engine.Cli.Api;
using XE_Local_AI_Engine.Cli.Rendering;
using XE_Local_AI_Engine.Cli.Streaming;

/// <summary><c>xe ask "prompt"</c>: one turn, the answer streamed to standard output, or JSON lines with <c>--json</c>.</summary>
internal static class AskCommand
{
    private const int MaxDerivedTitleChars = 60;

    public static readonly Option<string?> Agent = new("--agent")
    {
        Description = "Run as this saved agent (id or name, see 'xe agents')."
    };

    public static readonly Option<string?> Model = new("--model")
    {
        Description = "Use this model id for the turn instead of the node's default."
    };

    public static readonly Option<bool> ShowReasoning = new("--show-reasoning")
    {
        Description = "Show the model's reasoning (dimmed) instead of a one-line 'thinking...' marker."
    };

    private static readonly Argument<string> Prompt = new("prompt")
    {
        Description = "What to ask."
    };

    private static readonly Option<string?> Conversation = new("--conversation")
    {
        Description = "Continue this conversation (id) instead of starting a new one."
    };

    private static readonly Option<string?> NewConversationTitle = new("--new-conversation-title")
    {
        Description = "Title for the new conversation (default: the start of the prompt)."
    };

    public static Command Create(CliEnvironment environment)
    {
        var command = new Command("ask",
            "Ask one question and stream the answer. Approvals and questions are prompted on a terminal; without one "
            + "(or with --json) an approval is denied and a question cancels the turn, and the exit code is 6.");
        command.Arguments.Add(Prompt);
        command.Options.Add(Agent);
        command.Options.Add(Model);
        command.Options.Add(Conversation);
        command.Options.Add(NewConversationTitle);
        command.Options.Add(ShowReasoning);
        CommandRunner.SetAction(command, environment, (parse, cancellationToken) => RunAsync(parse, environment, cancellationToken));
        return command;
    }

    /// <summary>A title from the prompt's first line, cut at a word boundary.</summary>
    public static string DeriveTitle(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var line = prompt.Trim().Split('\n', 2)[0].Trim();
        if (line.Length <= MaxDerivedTitleChars)
        {
            return line;
        }

        var cut = line.LastIndexOf(' ', MaxDerivedTitleChars);
        return string.Concat(line.AsSpan(0, cut > 0 ? cut : MaxDerivedTitleChars), "...");
    }

    private static async Task<int> RunAsync(ParseResult parse, CliEnvironment environment, CancellationToken cancellationToken)
    {
        var prompt = parse.GetValue(Prompt) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new CliCommandException(ExitCodes.Usage, "The prompt is empty.");
        }

        var json = parse.GetValue(CliOptions.Json);
        using var linked = CommandRunner.LinkInterrupt(environment, cancellationToken);
        using var session = await NodeSession.OpenAsync(parse, environment, linked.Token);
        var agent = await AgentLookup.ResolveAsync(session.Api, parse.GetValue(Agent), linked.Token);
        var conversation = parse.GetValue(Conversation) is { } existing
            ? await session.Api.GetConversationAsync(AgentLookup.ParseConversationId(existing), linked.Token)
            : await session.Api.CreateConversationAsync(new CreateConversationRequest
            {
                Title = parse.GetValue(NewConversationTitle) ?? DeriveTitle(prompt),
                AgentDefinitionId = agent?.Id
            }, linked.Token);
        await using var chat = await ChatSession.ConnectAsync(session.Connection, session.Auth, session.Api, environment.TimeProvider, linked.Token);

        var request = new NodeChatStreamRequest
        {
            ConversationId = conversation.ConversationId,
            Content = prompt,
            Model = parse.GetValue(Model),
            AgentDefinitionId = agent?.Id ?? conversation.AgentDefinitionId,
            MessageId = Guid.NewGuid(),
            RequestId = Guid.NewGuid()
        };
        using var jsonRenderer = json ? new JsonTurnRenderer(environment.Out) : null;
        using var consoleRenderer = json
            ? null
            : new ConsoleTurnRenderer(environment.Out, environment.ErrorTerminal, sameScreen: false, parse.GetValue(ShowReasoning), environment.TimeProvider);
        var interactive = !json && environment.InputIsTerminal && environment.OutputIsTerminal;
        var runner = new TurnRunner(session.Api,
            (ITurnRenderer?)jsonRenderer ?? consoleRenderer!,
            interactive ? new TurnPrompter(environment.ErrorTerminal) : null,
            environment.TimeProvider);
        var outcome = await runner.RunAsync(token => chat.SendAsync(request, token),
            new CancelMessageRequest
            {
                ConversationId = request.ConversationId,
                MessageId = request.MessageId.Value,
                RequestId = request.RequestId.Value
            },
            environment.Interrupts.Token,
            cancellationToken);
        return outcome.ExitCode;
    }
}
