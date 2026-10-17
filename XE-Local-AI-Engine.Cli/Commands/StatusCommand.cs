namespace XE_Local_AI_Engine.Cli.Commands;

using System.CommandLine;
using System.Globalization;
using Spectre.Console;
using XE_Local_AI_Engine.Cli.Rendering;

/// <summary><c>xe status</c>: which node this client attaches to, and what it reports about itself.</summary>
internal static class StatusCommand
{
    public static Command Create(CliEnvironment environment)
    {
        var command = new Command("status", "Show the node this client attaches to: origin, version, vault and runtime.");
        CommandRunner.SetAction(command, environment, (parse, cancellationToken) => RunAsync(parse, environment, cancellationToken));
        return command;
    }

    private static async Task<int> RunAsync(ParseResult parse, CliEnvironment environment, CancellationToken cancellationToken)
    {
        using var linked = CommandRunner.LinkInterrupt(environment, cancellationToken);
        using var session = await NodeSession.OpenAsync(parse, environment, linked.Token);
        var info = await session.Api.GetNodeInfoAsync(linked.Token);
        var status = await session.Auth.GetStatusAsync(linked.Token);
        if (parse.GetValue(CliOptions.Json))
        {
            await JsonTurnRenderer.WriteLineAsync(environment.Out,
                new StatusJson
                {
                    Origin = session.Node.Origin.GetLeftPart(UriPartial.Authority),
                    ProcessId = session.Node.ProcessId,
                    DataDirectory = session.Node.DataDirectory,
                    SetupRequired = status.SetupRequired,
                    Vault = status.Vault,
                    Node = info
                },
                linked.Token);
            return ExitCodes.Success;
        }

        var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
        void Row(string name, string? value) =>
            grid.AddRow(new Text(name, new Style(decoration: Decoration.Bold)), new Text(string.IsNullOrEmpty(value) ? "-" : value));
        Row("Origin", session.Node.Origin.GetLeftPart(UriPartial.Authority));
        Row("Version", info.Commit is { Length: > 0 } commit ? $"{info.Version} ({commit})" : info.Version);
        Row("Flavour", info.Flavour);
        Row("OS", info.OsDescription);
        Row("GPU", info.GpuVendor);
        Row("Inference", info.InferenceBackend);
        Row("Uptime", TimeSpan.FromSeconds(info.UptimeSeconds).ToString("c", CultureInfo.InvariantCulture));
        Row("Vault", status.Vault);
        Row("Data directory", session.Node.DataDirectory);
        Row("Process", session.Node.ProcessId?.ToString(CultureInfo.InvariantCulture));
        foreach (var warning in info.Warnings)
        {
            Row("Warning", warning);
        }

        environment.Terminal.Write(grid);
        return ExitCodes.Success;
    }
}
