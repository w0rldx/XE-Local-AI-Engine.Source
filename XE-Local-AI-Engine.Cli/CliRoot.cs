namespace XE_Local_AI_Engine.Cli;

using System.CommandLine;
using System.Reflection;
using XE_Local_AI_Engine.Cli.Commands;

/// <summary>Builds the <c>xe</c> command tree; every command family adds its subcommand in <see cref="Create(CliEnvironment)" />.</summary>
internal static class CliRoot
{
    /// <summary>The informational version stamped at build time, or <c>unknown</c> when the attribute is missing.</summary>
    internal static string InformationalVersion { get; } =
        typeof(CliRoot).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    internal static RootCommand Create() =>
        Create(CliEnvironment.FromProcess());

    internal static RootCommand Create(CliEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var root = new RootCommand("XE Local AI Engine terminal client: talks to a node running on this machine. " + ExitCodes.HelpText);
        CliOptions.AddTo(root);
        root.Subcommands.Add(CreateVersionCommand());
        root.Subcommands.Add(StatusCommand.Create(environment));
        root.Subcommands.Add(ModelsCommand.Create(environment));
        root.Subcommands.Add(AgentsCommand.Create(environment));
        root.Subcommands.Add(AskCommand.Create(environment));
        root.Subcommands.Add(ChatCommand.Create(environment));
        return root;
    }

    /// <summary>Parses and runs <paramref name="args" />; a parse error exits 2, and Ctrl-C is left to the commands.</summary>
    internal static async Task<int> RunAsync(string[] args, CliEnvironment environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var parse = Create(environment).Parse(args);
        var exitCode = await parse.InvokeAsync(new InvocationConfiguration
        {
            Output = environment.Out,
            Error = environment.Error,
            ProcessTerminationTimeout = null
        }, cancellationToken);
        return parse.Errors.Count > 0 ? ExitCodes.Usage : exitCode;
    }

    private static Command CreateVersionCommand()
    {
        var command = new Command("version", "Print the client version.");
        command.SetAction(static (parseResult, _) => parseResult.InvocationConfiguration.Output.WriteLineAsync(InformationalVersion));
        return command;
    }
}
