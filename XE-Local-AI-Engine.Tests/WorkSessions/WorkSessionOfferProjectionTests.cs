namespace XE_Local_AI_Engine.Tests.WorkSessions;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The offer projection for the four state tools. Registering a handler in DI surfaces it in the RESOLUTION seam
///     only; without the merge here the seeded work-session agents would intersect to an empty tool set and the whole
///     feature would be inert — the trap <c>spawn_subagent</c> and the coder tools each hit before.
/// </summary>
public sealed class WorkSessionOfferProjectionTests
{
    [Test]
    public void GetOfferedTools_OmitsTheStateTools()
    {
        // Profile-opt-in only, exactly like spawn_subagent: an ordinary chat turn must not be offered them.
        var provider = CreateProvider("qwen3:8b");

        var offered = provider.GetOfferedTools("qwen3:8b");

        foreach (var name in WorkSessionToolDefinitions.ToolNames)
        {
            AssertEx.False(offered.Any(tool => tool.Name == name), $"{name} must be held out of the whole chat offer.");
        }
    }

    [Test]
    public void GetOfferedToolsForProfile_OffersAllFour()
    {
        var provider = CreateProvider("qwen3:8b");

        var offered = provider.GetOfferedToolsForProfile("qwen3:8b");

        foreach (var name in WorkSessionToolDefinitions.ToolNames)
        {
            AssertEx.Contains(offered, tool => tool.Name == name, $"{name} must reach a profile that opted in, or the seeded agents intersect to nothing.");
        }
    }

    [Test]
    public async Task GetOfferedToolsForProfileAsync_OffersAllFour()
    {
        var provider = CreateProvider("qwen3:8b");

        var offered = await provider.GetOfferedToolsForProfileAsync("qwen3:8b", isCloudModel: false).ConfigureAwait(false);

        foreach (var name in WorkSessionToolDefinitions.ToolNames)
        {
            AssertEx.Contains(offered, tool => tool.Name == name);
        }
    }

    [Test]
    public void GetOfferedToolsForProfile_WhenTheModelCannotCallTools_OffersNone()
    {
        var provider = CreateProvider("qwen3:8b");

        var offered = provider.GetOfferedToolsForProfile("a-model-that-is-not-capable");

        foreach (var name in WorkSessionToolDefinitions.ToolNames)
        {
            AssertEx.False(offered.Any(tool => tool.Name == name), "The opt-in must not bypass the capability gate.");
        }
    }

    [Test]
    public void GetKnownToolNames_ContainsAllFour_SoAgentCrudCanValidateThem()
    {
        var provider = CreateProvider("qwen3:8b");

        var names = provider.GetKnownToolNames();

        foreach (var name in WorkSessionToolDefinitions.ToolNames)
        {
            AssertEx.Contains(names, name);
        }
    }

    [Test]
    public void GetKnownTools_TagsTheStateToolsAsBuiltInWrites()
    {
        var provider = CreateProvider("qwen3:8b");

        var entries = provider.GetKnownTools();

        foreach (var name in WorkSessionToolDefinitions.ToolNames)
        {
            var entry = AssertEx.NotNull(entries.FirstOrDefault(candidate => candidate.Name == name), $"{name} must appear in the tool picker.");
            AssertEx.Equal(ToolCategory.WriteExecute, entry.Category);
            AssertEx.False(entry.RequiresApproval);
        }
    }

    private static LocalToolOfferProvider CreateProvider(params string[] toolCapableModels)
    {
        var registry = new FakeAgentToolRegistry([
            new LocalChatToolDescriptor(AgentHomeToolDefinition.ToolName, "Runs an agent task.", "{\"type\":\"object\"}", RequiresApproval: true),
            new LocalChatToolDescriptor("open_url", "Opens a URL.", "{\"type\":\"object\"}", RequiresApproval: false)
        ]);

        return new LocalToolOfferProvider(registry,
            new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
            StubNodeRuntimeSettings.Create().WithToolCapableModels(toolCapableModels).Build(),
            NullCustomToolScopeFactory.Instance,
            new FakeModelTrustResolver(),
            allowCloudKnowledgeAccess: false);
    }

    private sealed class FakeAgentToolRegistry(IReadOnlyList<LocalChatToolDescriptor> descriptors) : IAgentToolRegistry
    {
        public IReadOnlyList<AITool> GetLocalChatTools() =>
            [];

        public IReadOnlyList<LocalChatToolDescriptor> GetLocalChatToolDescriptors() =>
            descriptors;
    }
}
