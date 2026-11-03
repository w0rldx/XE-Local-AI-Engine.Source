namespace XE_Local_AI_Engine.Cli.Commands;

using System.CommandLine;
using System.Globalization;
using Spectre.Console;
using XE_Local_AI_Engine.Cli.Api;
using XE_Local_AI_Engine.Cli.Rendering;

/// <summary><c>xe models</c>: the installed models, and which of them are loaded right now.</summary>
internal static class ModelsCommand
{
    public static Command Create(CliEnvironment environment)
    {
        var command = new Command("models", "List the node's installed models and the ones currently loaded.");
        CommandRunner.SetAction(command, environment, (parse, cancellationToken) => RunAsync(parse, environment, cancellationToken));
        return command;
    }

    /// <summary>Installed models first, marked running when loaded; loaded models the installed list lacks follow, sorted by name.</summary>
    public static IReadOnlyList<ModelJson> Merge(LocalModelList installed, RunningModelList running)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(running);

        // One row per model: a model loaded for several roles has several processes. An exited process waiting to be reaped is not loaded.
        var loaded = running.Items.Where(static process => process.DetailCode != RunningModel.ExitedDetailCode)
                            .Select(static process => process.ModelName)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = installed.Items.Select(model => new ModelJson
        {
            ModelName = model.ModelName,
            Provider = model.Provider,
            Kind = model.Kind,
            ParameterSize = model.ParameterSize,
            QuantizationLevel = model.QuantizationLevel,
            SizeBytes = model.SizeBytes,
            IsSelected = model.IsSelected,
            IsReasoningCapable = model.IsReasoningCapable,
            IsToolCapable = model.IsToolCapable,
            Installed = true,
            Running = loaded.Contains(model.ModelName)
        }).ToList();
        var named = rows.Select(static row => row.ModelName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        rows.AddRange(loaded.Where(modelName => !named.Contains(modelName)).Order(StringComparer.OrdinalIgnoreCase).Select(static modelName => new ModelJson
        {
            ModelName = modelName,
            Running = true
        }));
        return rows;
    }

    private static async Task<int> RunAsync(ParseResult parse, CliEnvironment environment, CancellationToken cancellationToken)
    {
        using var linked = CommandRunner.LinkInterrupt(environment, cancellationToken);
        using var session = await NodeSession.OpenAsync(parse, environment, linked.Token);
        var installed = await session.Api.ListModelsAsync(linked.Token);
        var running = await session.Api.ListRunningModelsAsync(linked.Token);
        var rows = Merge(installed, running);
        if (parse.GetValue(CliOptions.Json))
        {
            await JsonTurnRenderer.WriteLineAsync(environment.Out, rows, linked.Token);
            return ExitCodes.Success;
        }

        var table = new Table().AddColumns("Model", "Kind", "Params", "Quant", "Size", "Default", "Loaded");
        foreach (var row in rows)
        {
            table.AddRow(Markup.Escape(row.ModelName),
                Markup.Escape(row.Kind ?? "-"),
                Markup.Escape(row.ParameterSize ?? "-"),
                Markup.Escape(row.QuantizationLevel ?? "-"),
                FormatBytes(row.SizeBytes),
                row.IsSelected ? "yes" : string.Empty,
                row.Running ? "yes" : string.Empty);
        }

        environment.Terminal.Write(table);
        return ExitCodes.Success;
    }

    private static string FormatBytes(long? bytes) =>
        bytes is { } value ? string.Create(CultureInfo.InvariantCulture, $"{value / (1024d * 1024 * 1024):0.0} GiB") : "-";
}
