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

    /// <summary>The designer can draw a DevTask only if it is told both actions, the project key a run needs, and the DEVTASK-1 shape an apply must sit in.</summary>
    [Test]
    [Arguments("`implement`")]
    [Arguments("`apply`")]
    [Arguments("`requirements` (required")]
    [Arguments("`task` (required")]
    [Arguments("run.input.developmentProjectId")]
    [Arguments("Rule DEVTASK-1: every edge into")]
    [Arguments("never in a Chat graph")]
    [Arguments("for a DevTask `apply` node: it reads its task by key")]
    public void StaticContract_DescribesTheDevTaskKindAndItsApplyRule(string expected) =>
        AssertEx.Contains(GraphWorkflowDesignerPrompt.StaticContract, expected);

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
