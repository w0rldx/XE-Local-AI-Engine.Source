namespace XE_Local_AI_Engine.Tests.Agents;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class AgentModelRequirementsTests
{
    [Test]
    public void RequiresTools_IsTrueForAnAgentThatListsTools()
    {
        AssertEx.True(AgentModelRequirements.RequiresTools(["read_file"], AgentDefinitionKind.Single));
    }

    [Test]
    public void RequiresTools_IsTrueForAnOrchestratorWithNoListedTools()
    {
        AssertEx.True(AgentModelRequirements.RequiresTools([], AgentDefinitionKind.Orchestrator), "handoffs are tool calls.");
    }

    [Test]
    public void RequiresTools_IsFalseForTheDefaultAssistantShape()
    {
        AssertEx.False(AgentModelRequirements.RequiresTools([], AgentDefinitionKind.Single), "a plain-chat agent requires nothing.");
    }

    [Test]
    public void ToolRefusal_NamesTheAgentAndTheModel_WhenAToolRequiringAgentMeetsANonToolModel()
    {
        var refusal = AgentModelRequirements.ToolRefusal("Log Summarizer", "tiny-model", supportsTools: false, requiresTools: true);

        AssertEx.Contains(refusal, "'Log Summarizer'");
        AssertEx.Contains(refusal, "'tiny-model'");
        AssertEx.Contains(refusal, "will not run unattended");
        AssertEx.Contains(refusal, "is not known to support", message: "Unknown and unresolved collapse to false, so the sentence must not over-claim.");
    }

    [Test]
    [Arguments(true, true)]
    [Arguments(false, false)]
    [Arguments(true, false)]
    public void ToolRefusal_IsNull_WhenTheModelCanCallToolsOrNoneAreNeeded(bool supportsTools, bool requiresTools)
    {
        AssertEx.Null(AgentModelRequirements.ToolRefusal("agent", "model", supportsTools, requiresTools));
    }
}
