namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Runs every sample the SPA's "New workflow" dialog posts through the server's own parser, the client validator
///     being only a mirror: a sample that passes here is one the create call accepts and a run can start from.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class GraphWorkflowSampleContractTests
{
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
    ///     Runnable on a fresh install: no Tool (needs a tool catalog), no Agent (needs agent setup), no ChatInput
    ///     (needs a chat binding), and no model pin, so every model call falls to the local default.
    /// </summary>
    [Test]
    public void EverySample_NeedsNothingButALocalChatModel()
    {
        GraphWorkflowNodeKind[] refused = [GraphWorkflowNodeKind.Tool, GraphWorkflowNodeKind.Agent, GraphWorkflowNodeKind.ChatInput];
        foreach (var (name, graph) in Samples())
        {
            var offending = graph.Nodes.Values.Where(node => refused.Contains(node.Kind)).Select(static node => node.NodeKey);
            AssertEx.Empty(offending, $"{name} carries a node that needs more than a local chat model.");

            var pinned = graph.Nodes.Values.Where(static node => node.Config switch
                                          {
                                              GraphWorkflowLlmCallConfig config => config.Model is not null,
                                              GraphWorkflowDecisionModelConfig config => config.Model is not null,
                                              _ => false
                                          })
                              .Select(static node => node.NodeKey);
            AssertEx.Empty(pinned, $"{name} pins a model; samples must use the local default.");
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
                                              GraphWorkflowEndConfig { ResultPath: { } resultPath } => [resultPath],
                                              _ => []
                                          })
                             .Concat(graph.Edges.Where(static edge => edge.Condition is not null).Select(static edge => edge.Condition!.Path))
                             .ToArray();

            AssertEx.NotEmpty(paths, $"{name} names no path at all; refusing a vacuous pass.");
            AssertEx.Empty(paths.Where(static path => !GraphWorkflowTokens.IsDotPath(path)), $"{name} carries a path that is not a dot path.");
        }
    }

    private static IReadOnlyList<(string Name, GraphWorkflowGraph Graph)> Samples()
    {
        var directory = RepositoryPaths.Combine("XE-Local-AI-Engine.Client.React", "src", "features", "graphWorkflows", "samples");
        var files = Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal).ToArray();
        AssertEx.NotEmpty(files, $"No sample graphs under {directory}; refusing a vacuous pass.");
        return [.. files.Select(static file => (Path.GetFileName(file), GraphWorkflowGraph.Parse(File.ReadAllText(file))))];
    }
}
