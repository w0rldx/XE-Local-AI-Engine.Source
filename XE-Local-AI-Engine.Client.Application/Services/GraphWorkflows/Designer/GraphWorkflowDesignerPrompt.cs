namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Designer;

using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Tools;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     The designer's system prompt: the embedded Markdown contract, then what only this node can say — the config
///     members each kind reads, the tools, models and agents a graph may name, the working graph and the selection.
/// </summary>
/// <remarks>
///     One system message, because some chat templates refuse a system message anywhere but first. The stable part leads
///     so a runtime's prompt cache reuses it across turns. <c>GraphWorkflowDesignerPromptTests</c> is the drift guard.
/// </remarks>
internal static class GraphWorkflowDesignerPrompt
{
    private const string ResourceNameSuffix = "GraphWorkflowDesignerPrompt.md";

    private static readonly Lazy<string> Contract = new(LoadContract, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The embedded Markdown part, as shipped.</summary>
    public static string StaticContract => Contract.Value;

    public static string Build(IReadOnlyList<InvocableToolDescriptor> tools,
        IReadOnlyList<LocalModelDescriptor> models,
        IReadOnlyList<AgentDefinitionRecord> agents,
        string graphJson,
        string? selectedNodeKey,
        GraphWorkflowDesignerIntent intent)
    {
        var builder = new StringBuilder(Contract.Value).AppendLine().AppendLine("# Config members per kind (the parser refuses any other member)");
        foreach (var (kind, members) in GraphWorkflowGraph.ConfigMembers.OrderBy(static entry => entry.Key.ToString(), StringComparer.Ordinal))
        {
            _ = builder.Append("- ").Append(kind).Append(": ").AppendLine(members.Length == 0 ? "(none, use {})" : string.Join(", ", members));
        }

        _ = builder.AppendLine().AppendLine("# Tools a Tool node may name");
        AppendList(builder, tools.Select(static tool => $"{tool.Name}{(tool.IsAction ? " (action)" : string.Empty)}: {tool.Description} Parameters: {tool.ParameterSchema}"));

        _ = builder.AppendLine().AppendLine("# Installed models a node may pin (or leave the pin null)");
        AppendList(builder, models.Select(static model => model.DecisionType is null ? model.ModelName : $"{model.ModelName} (decision model, for the systemone provider)"));

        _ = builder.AppendLine().AppendLine("# Agents an Agent node may bind by agentDefinitionId");
        AppendList(builder, agents.Select(static agent => $"{agent.Id}: {agent.Name}"));

        _ = builder.AppendLine().AppendLine("# The working graph on the canvas").AppendLine("```json").AppendLine(graphJson).AppendLine("```");
        if (!string.IsNullOrWhiteSpace(selectedNodeKey))
        {
            _ = builder.Append("The operator has selected node '").Append(selectedNodeKey).AppendLine("'; \"this node\" means that one.");
        }

        _ = builder.AppendLine().AppendLine(intent switch
        {
            GraphWorkflowDesignerIntent.Ask => "This turn is a question: answer in \"reply\" and set \"proposal\" to null.",
            GraphWorkflowDesignerIntent.Change => "This turn asks for a change: put the whole proposed graph in \"proposal\".",
            _ => "Decide from the operator's last message whether a change is asked for."
        });
        return builder.ToString();
    }

    private static void AppendList(StringBuilder builder, IEnumerable<string> lines)
    {
        var any = false;
        foreach (var line in lines)
        {
            _ = builder.Append("- ").AppendLine(line);
            any = true;
        }

        if (!any)
        {
            _ = builder.AppendLine("(none)");
        }
    }

    private static string LoadContract()
    {
        var assembly = typeof(GraphWorkflowDesignerPrompt).Assembly;
        var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(static name => name.EndsWith(ResourceNameSuffix, StringComparison.Ordinal))
                           ?? throw new InvalidOperationException($"Embedded resource '{ResourceNameSuffix}' was not found in assembly '{assembly.GetName().Name}'.");

#pragma warning disable MA0045 // Lazy initializer, which cannot await; the resource is read once per process.
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' could not be opened.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
#pragma warning restore MA0045
    }
}
