namespace XE_Local_AI_Engine.Cli.Commands;

using System.CommandLine;
using Spectre.Console;
using XE_Local_AI_Engine.Cli.Rendering;

/// <summary><c>xe agents</c>: the saved agents a chat or an ask can run as.</summary>
internal static class AgentsCommand
{
    public static Command Create(CliEnvironment environment)
    {
        var command = new Command("agents", "List the node's saved agents (use the id or name with --agent).");
        CommandRunner.SetAction(command, environment, (parse, cancellationToken) => RunAsync(parse, environment, cancellationToken));
        return command;
    }

    private static async Task<int> RunAsync(ParseResult parse, CliEnvironment environment, CancellationToken cancellationToken)
    {
        using var linked = CommandRunner.LinkInterrupt(environment, cancellationToken);
        using var session = await NodeSession.OpenAsync(parse, environment, linked.Token);
        var agents = (await session.Api.ListAgentsAsync(linked.Token)).Items;
        if (parse.GetValue(CliOptions.Json))
        {
            await JsonTurnRenderer.WriteLineAsync(environment.Out, agents, linked.Token);
            return ExitCodes.Success;
        }

        var table = new Table().AddColumns("Id", "Name", "Kind", "Tools", "Description");
        foreach (var agent in agents)
        {
            table.AddRow(agent.Id.ToString("D"),
                Markup.Escape(agent.Name) + (agent.IsDefaultAssistant ? " [grey](default)[/]" : string.Empty),
                Markup.Escape(agent.Kind),
                Markup.Escape(string.Join(", ", agent.AllowedToolNames)),
                Markup.Escape(agent.Description ?? string.Empty));
        }

        environment.Terminal.Write(table);
        return ExitCodes.Success;
    }
}
