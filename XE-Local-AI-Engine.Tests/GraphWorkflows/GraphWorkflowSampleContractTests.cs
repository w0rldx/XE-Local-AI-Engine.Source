namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Text.Json.Nodes;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Runs every sample the SPA's "New workflow" dialog posts through the server's own parser, the client validator
///     being only a mirror: a sample that passes here is one the create call accepts and a run can start from.
/// </summary>
/// <remarks>
///     The parse is the save-time check, <c>DEVTASK-1</c> included; <c>GraphWorkflowDevelopmentSampleTests</c> saves and
///     starts the samples on a host, which adds the lint, the service warnings and the run-start project binding.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class GraphWorkflowSampleContractTests
{
    /// <summary>What the samples' unbound <c>repository</c> nodes are bound to here, standing in for the agent the dialog binds.</summary>
    private static readonly Guid DialogBoundAgentId = Guid.Parse("5a3c0de0-0000-4000-8000-000000000001");

    [Test]
    public void EverySample_ParsesAsAStandardGraphWithAnEnd()
    {
        foreach (var (name, graph) in Samples())
        {
            AssertEx.Equal(GraphWorkflowDefinitionKind.Standard, graph.Kind, $"{name} must be a Standard graph.");
            AssertEx.Contains(graph.Nodes.Values, static node => node.Kind == GraphWorkflowNodeKind.End, $"{name} needs an End node.");
        }
    }

    /// <summary>A shipped sample must not open with a note the operator did not cause, so it also raises no warning.</summary>
    [Test]
    public void EverySample_RaisesNoWarning()
    {
        foreach (var (name, graph) in Samples())
        {
            AssertEx.Empty(graph.Warnings, $"{name} warned: {string.Join(" | ", graph.Warnings)}");
        }
    }

    /// <summary>
    ///     Runnable on a fresh install: no Tool, no ChatInput, no model pin, and an Agent only when unbound (it then runs
    ///     the built-in default persona) or bound by the dialog to the read-only repository agent.
    /// </summary>
    [Test]
    public void EverySample_NeedsNothingButALocalChatModel()
    {
        GraphWorkflowNodeKind[] refused = [GraphWorkflowNodeKind.Tool, GraphWorkflowNodeKind.ChatInput];
        foreach (var (name, graph) in Samples())
        {
            var offending = graph.Nodes.Values.Where(node => refused.Contains(node.Kind)
                                                             || node.Config is GraphWorkflowAgentConfig { AgentDefinitionId: { } bound } && bound != DialogBoundAgentId)
                                 .Select(static node => node.NodeKey);
            AssertEx.Empty(offending, $"{name} carries a node that needs more than a local chat model.");

            var pinned = graph.Nodes.Values.Where(static node => node.Config switch
                              {
                                  GraphWorkflowLlmCallConfig config => config.Model is not null,
                                  GraphWorkflowDecisionModelConfig config => config.Model is not null,
                                  GraphWorkflowAgentConfig config => config.Model is not null,
                                  _ => false
                              })
                              .Select(static node => node.NodeKey);
            AssertEx.Empty(pinned, $"{name} pins a model; samples must use the local default.");
        }
    }

    /// <summary>
    ///     The Development samples replace the Dev Workflows templates: <c>dev-intake</c> carries the implement and the
    ///     apply <c>DEVTASK-1</c> judged in the parse above, and <c>research-plan-approval</c> implements nothing.
    /// </summary>
    [Test]
    public void TheDevelopmentSamples_ReadTheRepositoryAndOnlyIntakeApplies()
    {
        var samples = Samples().ToDictionary(static sample => sample.Name, static sample => sample.Graph, StringComparer.Ordinal);
        var intake = samples["dev-intake.json"];
        AssertEx.ContainsSingle(intake.Nodes.Values, static node => node.Config is GraphWorkflowDevTaskConfig { IsApply: true });
        var apply = intake.Nodes.Values.Single(static node => node.Config is GraphWorkflowDevTaskConfig { IsApply: true });
        AssertEx.Equal("implement", ((GraphWorkflowDevTaskConfig)apply.Config).TaskNodeKey);
        AssertEx.Equal(GraphWorkflowNodeKind.Pause, intake.Nodes[intake.Edges.Single(edge => edge.To == apply.NodeKey).From].Kind, "the apply is reached from a Pause only.");

        var research = samples["research-plan-approval.json"];
        AssertEx.Empty(research.Nodes.Values.Where(static node => node.Kind == GraphWorkflowNodeKind.DevTask), "research-plan-approval implements nothing.");
        foreach (var graph in new[]
                 {
                     intake,
                     research
                 })
        {
            AssertEx.Contains(graph.Nodes.Values, static node => node.Config is GraphWorkflowAgentConfig { Repository: true }, "a Development sample reads its project's repository.");

            // A plan a DevTask could be fed must already obey DevelopmentTestWritePolicy, or its reviewer refuses the change.
            var plan = graph.Nodes["plan"].Config switch
            {
                GraphWorkflowAgentConfig config => config.Instructions,
                GraphWorkflowLlmCallConfig config => config.Prompt,
                _ => null
            };
            AssertEx.Contains(AssertEx.NotNull(plan), "existing test files cannot be modified");
        }
    }

    /// <summary>
    ///     The parser already refuses a bad binding or condition path; End's <c>resultPath</c> is the one it would
    ///     accept and then silently resolve to <c>null</c> when it is not a dot path, so all three are checked here.
    /// </summary>
    [Test]
    public void EverySamplePath_IsADotPath()
    {
        foreach (var (name, graph) in Samples())
        {
            var paths = graph.Nodes.Values.SelectMany(static node => node.Config switch
                             {
                                 GraphWorkflowLlmCallConfig config => config.InputBindings.Values,
                                 GraphWorkflowDecisionModelConfig config => config.InputBindings.Values,
                                 GraphWorkflowDevTaskConfig config => config.InputBindings.Values,
                                 GraphWorkflowEndConfig { ResultPath: { } resultPath } => [resultPath],
                                 _ => []
                             })
                             .Concat(graph.Edges.Where(static edge => edge.Condition is not null).Select(static edge => edge.Condition!.Path))
                             .ToArray();

            AssertEx.NotEmpty(paths, $"{name} names no path at all; refusing a vacuous pass.");
            AssertEx.Empty(paths.Where(static path => !GraphWorkflowTokens.IsDotPath(path)), $"{name} carries a path that is not a dot path.");
        }
    }

    /// <summary>Every shipped sample file, by file name.</summary>
    internal static IReadOnlyList<string> SampleFiles()
    {
        var directory = RepositoryPaths.Combine("XE-Local-AI-Engine.Client.React", "src", "features", "graphWorkflows", "samples");
        var files = Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal).ToArray();
        AssertEx.NotEmpty(files, $"No sample graphs under {directory}; refusing a vacuous pass.");
        return files;
    }

    /// <summary>
    ///     A sample's graph as the dialog posts it: every <c>repository</c> Agent node that names no agent is bound to
    ///     <paramref name="repositoryAgentId" />, as the SPA's <c>bindRepositoryAgent</c> binds it.
    /// </summary>
    internal static string PostedGraphJson(string file, Guid repositoryAgentId)
    {
        var root = AssertEx.NotNull(JsonNode.Parse(File.ReadAllText(file)));
        foreach (var node in AssertEx.NotNull(root["nodes"]).AsArray())
        {
            if (node?["config"] is JsonObject config
                && (string?)node["kind"] == "Agent"
                && config["repository"]?.GetValue<bool>() == true
                && config["agentDefinitionId"] is null)
            {
                config["agentDefinitionId"] = repositoryAgentId.ToString("D");
            }
        }

        return root.ToJsonString();
    }

    private static IReadOnlyList<(string Name, GraphWorkflowGraph Graph)> Samples() =>
        [.. SampleFiles().Select(static file => (Path.GetFileName(file), GraphWorkflowGraph.Parse(PostedGraphJson(file, DialogBoundAgentId))))];
}
