namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Designer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The drift guard on the designer's system prompt: every node kind is described and every config member the parser reads is listed.</summary>
[Category(TestCategories.Unit)]
public sealed class GraphWorkflowDesignerPromptTests
{
    [Test]
    public void StaticContract_NamesEveryNodeKind()
    {
        var missing = Enum.GetNames<GraphWorkflowNodeKind>().Where(static kind => !GraphWorkflowDesignerPrompt.StaticContract.Contains($"`{kind}`", StringComparison.Ordinal));

        AssertEx.Empty(missing, "every node kind needs its own entry in GraphWorkflowDesignerPrompt.md");
    }

    [Test]
    public void BuiltPrompt_ListsEveryConfigMemberOfEveryKind()
    {
        var prompt = GraphWorkflowDesignerPrompt.Build([], [], [], "{}", selectedNodeKey: null, GraphWorkflowDesignerIntent.Auto);

        AssertEx.Equal(Enum.GetValues<GraphWorkflowNodeKind>().Length, GraphWorkflowGraph.ConfigMembers.Count, "every kind has a member list to render");
        foreach (var (kind, members) in GraphWorkflowGraph.ConfigMembers)
        {
            var line = prompt.Split('\n').FirstOrDefault(candidate => candidate.StartsWith($"- {kind}: ", StringComparison.Ordinal));
            AssertEx.NotNull(line, $"the prompt has no member line for {kind}");
            AssertEx.Empty(members.Where(member => !line!.Contains(member, StringComparison.Ordinal)), $"{kind}'s line misses a member: {line}");
        }
    }

    /// <summary>The worked example teaches the model by imitation, so it must itself be a clean graph.</summary>
    [Test]
    public void StaticContract_ExampleGraph_ParsesWithoutAWarning()
    {
        var contract = GraphWorkflowDesignerPrompt.StaticContract;
        var start = contract.LastIndexOf("```json", StringComparison.Ordinal) + "```json".Length;
        var example = contract[start..contract.IndexOf("```", start, StringComparison.Ordinal)];

        var graph = GraphWorkflowGraph.Parse(example);

        AssertEx.Empty(graph.Warnings, $"the example warned: {string.Join(" | ", graph.Warnings)}");
    }
}
