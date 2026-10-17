namespace XE_Local_AI_Engine.Cli.Commands;

using System.CommandLine;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Spectre.Console;
using XE_Local_AI_Engine.Cli.Api;
using XE_Local_AI_Engine.Cli.Rendering;
using XE_Local_AI_Engine.Cli.Streaming;

/// <summary><c>xe chat</c>: an interactive session on one conversation, with slash commands and Ctrl-C to stop a reply.</summary>
/// <remarks>
///     A line ending in a backslash continues on the next line. Ctrl-C during a reply cancels the turn on the node and
///     returns to the prompt; Ctrl-C at the prompt, <c>/quit</c> or end of input leaves with exit 0.
/// </remarks>
internal sealed class ChatCommand
{
    internal const string HelpText =
        "/new [title]  start a new conversation\n"
        + "/agent <id|name>  run the next turns as this agent (/agent alone clears it, in a new conversation when this one has an agent)\n"
        + "/model <id>  use this model for the next turns (/model alone clears it)\n"
        + "/conversations  list recent conversations\n"
        + "/resume  re-attach to a turn still running in this conversation\n"
        + "/cancel  cancel the last turn on the node\n"
        + "/quit  leave";

    private const int ConversationListLimit = 20;

    private static readonly Option<string?> Conversation = new("--conversation")
    {
        Description = "Open this conversation (id) and re-attach to a turn that is still running in it."
    };

    private readonly CliEnvironment _environment;
    private readonly NodeSession _session;
    private readonly ChatSession _chat;
    private readonly bool _showReasoning;
    private Guid? _conversationId;
    private AgentDefinition? _agent;
    private Guid? _conversationAgentId;
    private string? _model;
    private CancelMessageRequest? _lastTurn;

    internal ChatCommand(CliEnvironment environment, NodeSession session, ChatSession chat, bool showReasoning)
    {
        _environment = environment;
        _session = session;
        _chat = chat;
        _showReasoning = showReasoning;
    }

    private IAnsiConsole Terminal => _environment.Terminal;

    public static Command Create(CliEnvironment environment)
    {
        var command = new Command("chat", "Chat interactively. Type /help inside for the slash commands. No --json mode: script with xe ask --json.");
        command.Options.Add(AskCommand.Agent);
        command.Options.Add(AskCommand.Model);
        command.Options.Add(Conversation);
        command.Options.Add(AskCommand.ShowReasoning);
        CommandRunner.SetAction(command, environment, (parse, cancellationToken) => RunAsync(parse, environment, cancellationToken));
        return command;
    }

    /// <summary>Splits a slash command into its name and the rest of the line, trimmed.</summary>
    public static SlashCommand ParseSlash(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var trimmed = line.Trim();
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return space < 0
            ? new SlashCommand(trimmed, string.Empty)
            : new SlashCommand(trimmed[..space], trimmed[(space + 1)..].Trim());
    }

    private static async Task<int> RunAsync(ParseResult parse, CliEnvironment environment, CancellationToken cancellationToken)
    {
        if (parse.GetValue(CliOptions.Json))
        {
            throw new CliCommandException(ExitCodes.Usage, "xe chat has no --json mode; use xe ask --json in scripts.");
        }

        if (!environment.InputIsTerminal || !environment.OutputIsTerminal)
        {
            throw new CliCommandException(ExitCodes.InteractionRequired, "xe chat needs an interactive terminal; use xe ask in scripts.");
        }

        ChatCommand chat;
        NodeSession session;
        ChatSession hub;
        using (var linked = CommandRunner.LinkInterrupt(environment, cancellationToken))
        {
            session = await NodeSession.OpenAsync(parse, environment, linked.Token);
            try
            {
                hub = await ChatSession.ConnectAsync(session.Connection, session.Auth, session.Api, environment.TimeProvider, linked.Token);
            }
            catch
            {
                session.Dispose();
                throw;
            }

            chat = new ChatCommand(environment, session, hub, parse.GetValue(AskCommand.ShowReasoning))
            {
                _model = parse.GetValue(AskCommand.Model)
            };
        }

        using (session)
            await using (hub)
            {
                using (var linked = CommandRunner.LinkInterrupt(environment, cancellationToken))
                {
                    chat._agent = await AgentLookup.ResolveAsync(session.Api, parse.GetValue(AskCommand.Agent), linked.Token);
                    if (parse.GetValue(Conversation) is { } existing)
                    {
                        await chat.OpenConversationAsync(AgentLookup.ParseConversationId(existing), linked.Token);
                    }
                }

                if (chat._conversationId is { } opened)
                {
                    await chat.RunTurnAsync(token => hub.ResumeConversationAsync(opened, token), turn: null, cancellationToken);
                }

                return await chat.LoopAsync(cancellationToken);
            }
    }

    private async Task<int> LoopAsync(CancellationToken cancellationToken)
    {
        Terminal.MarkupLine($"[grey]Connected to {Markup.Escape(_session.Node.Origin.GetLeftPart(UriPartial.Authority))}. /help lists the commands; Ctrl-C at the prompt leaves.[/]");
        while (true)
        {
            var interrupt = _environment.Interrupts.Reset();
            string input;
            try
            {
                input = await ReadInputAsync(interrupt);
            }
            catch (OperationCanceledException) when (interrupt.IsCancellationRequested)
            {
                return ExitCodes.Success;
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            try
            {
                if (input.TrimStart().StartsWith('/'))
                {
                    if (!await HandleSlashAsync(ParseSlash(input), cancellationToken))
                    {
                        return ExitCodes.Success;
                    }

                    continue;
                }

                await SendAsync(input, cancellationToken);
            }
            catch (Exception exception) when (CommandRunner.ExitCodeFor(exception) is not null and not ExitCodes.AuthOrVault)
            {
                Terminal.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
            }
        }
    }

    private async Task<string> ReadInputAsync(CancellationToken interrupt)
    {
        var text = new StringBuilder();
        var line = await Terminal.PromptAsync(new TextPrompt<string>("[bold]you>[/]").AllowEmpty(), interrupt);
        while (line.EndsWith('\\'))
        {
            text.Append(line, 0, line.Length - 1).Append('\n');
            line = await Terminal.PromptAsync(new TextPrompt<string>("[grey]...[/]").AllowEmpty(), interrupt);
        }

        return text.Append(line).ToString();
    }

    /// <summary>Runs one slash command; false means leave the session.</summary>
    internal async Task<bool> HandleSlashAsync(SlashCommand command, CancellationToken cancellationToken)
    {
        using var linked = CommandRunner.LinkInterrupt(_environment, cancellationToken);
        switch (command.Name)
        {
            case "/quit" or "/exit":
                return false;
            case "/help":
                Terminal.WriteLine(HelpText);
                break;
            case "/new":
                await StartConversationAsync(command.Argument.Length > 0 ? command.Argument : null, linked.Token);
                break;
            case "/agent":
                _agent = await AgentLookup.ResolveAsync(_session.Api, command.Argument, linked.Token);
                if (_agent is null && _conversationAgentId is not null)
                {
                    // The node falls back to the conversation's stored agent, so only a conversation without one clears it.
                    await StartConversationAsync(title: null, linked.Token);
                    Terminal.MarkupLine($"[grey]Agent cleared: new conversation {_conversationId:D} without an agent.[/]");
                    break;
                }

                Terminal.MarkupLine(_agent is null ? "[grey]Agent cleared.[/]" : $"[grey]Agent: {Markup.Escape(_agent.Name)}[/]");
                break;
            case "/model":
                _model = command.Argument.Length > 0 ? command.Argument : null;
                Terminal.MarkupLine($"[grey]Model: {Markup.Escape(_model ?? "node default")}[/]");
                break;
            case "/conversations":
                await ListConversationsAsync(linked.Token);
                break;
            case "/resume" when _conversationId is { } conversationId:
                await RunTurnAsync(token => _chat.ResumeConversationAsync(conversationId, token), turn: null, cancellationToken);
                break;
            case "/cancel" when _lastTurn is { } turn:
                var answer = await _session.Api.CancelAsync(turn, linked.Token);
                Terminal.MarkupLine($"[grey]Cancel: {Markup.Escape(answer.Status)}[/]");
                break;
            case "/resume" or "/cancel":
                Terminal.MarkupLine("[grey]No conversation turn yet.[/]");
                break;
            default:
                Terminal.MarkupLine($"[yellow]Unknown command {Markup.Escape(command.Name)}; /help lists them.[/]");
                break;
        }

        return true;
    }

    private async Task OpenConversationAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = await _session.Api.GetConversationAsync(conversationId, cancellationToken);
        _conversationId = conversation.ConversationId;
        _conversationAgentId = conversation.AgentDefinitionId;
        Terminal.MarkupLine($"[grey]Conversation: {Markup.Escape(conversation.Title ?? conversation.ConversationId.ToString("D"))} ({conversation.Messages.Count} messages)[/]");
    }

    private async Task StartConversationAsync(string? title, CancellationToken cancellationToken)
    {
        var created = await _session.Api.CreateConversationAsync(new CreateConversationRequest
        {
            Title = title,
            AgentDefinitionId = _agent?.Id
        }, cancellationToken);
        _conversationId = created.ConversationId;
        _conversationAgentId = created.AgentDefinitionId;
        _lastTurn = null;
        Terminal.MarkupLine($"[grey]New conversation {created.ConversationId:D}[/]");
    }

    private async Task ListConversationsAsync(CancellationToken cancellationToken)
    {
        var list = await _session.Api.ListConversationsAsync(includeArchived: false, ConversationListLimit, cancellationToken);
        var table = new Table().AddColumns("Id", "Title", "Last activity");
        foreach (var item in list.Items)
        {
            table.AddRow(item.ConversationId.ToString("D"),
                Markup.Escape(item.Title ?? string.Empty),
                TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(item.LastSeenUtc), _environment.TimeProvider.LocalTimeZone)
                            .ToString("g", CultureInfo.CurrentCulture));
        }

        Terminal.Write(table);
    }

    private async Task SendAsync(string input, CancellationToken cancellationToken)
    {
        if (_conversationId is null)
        {
            using var linked = CommandRunner.LinkInterrupt(_environment, cancellationToken);
            await StartConversationAsync(AskCommand.DeriveTitle(input), linked.Token);
        }

        var request = new NodeChatStreamRequest
        {
            ConversationId = _conversationId!.Value,
            Content = input,
            Model = _model,
            AgentDefinitionId = _agent?.Id ?? _conversationAgentId,
            MessageId = Guid.NewGuid(),
            RequestId = Guid.NewGuid()
        };
        await RunTurnAsync(token => _chat.SendAsync(request, token),
            new CancelMessageRequest
            {
                ConversationId = request.ConversationId,
                MessageId = request.MessageId.Value,
                RequestId = request.RequestId.Value
            },
            cancellationToken);
    }

    internal async Task RunTurnAsync(Func<CancellationToken, IAsyncEnumerable<CliStreamEvent>> open, CancelMessageRequest? turn, CancellationToken cancellationToken)
    {
        using var renderer = new ConsoleTurnRenderer(_environment.Out, Terminal, sameScreen: true, _showReasoning, _environment.TimeProvider);
        var runner = new TurnRunner(_session.Api, renderer, new TurnPrompter(Terminal), _environment.TimeProvider);
        TurnOutcome outcome;
        try
        {
            outcome = await runner.RunAsync(open, turn, _environment.Interrupts.Reset(), cancellationToken);
        }
        catch (Exception exception) when (exception is HubException or ChatStreamWatchdogException)
        {
            Terminal.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/] [grey]/resume re-attaches if the turn is still running.[/]");
            _lastTurn = turn ?? _lastTurn;
            return;
        }

        if (outcome.Terminal is { } terminal)
        {
            _lastTurn = new CancelMessageRequest
            {
                ConversationId = terminal.ConversationId,
                MessageId = terminal.MessageId,
                RequestId = terminal.RequestId
            };
        }
        else if (turn is null)
        {
            Terminal.MarkupLine("[grey]Nothing is running in this conversation.[/]");
        }
        else
        {
            // Ctrl-C's cancel failed or its grace ran out: the turn may still run, so /cancel must target it.
            _lastTurn = turn;
        }
    }

    /// <summary>A parsed <c>/command argument</c> line.</summary>
    internal readonly record struct SlashCommand(string Name, string Argument);
}
