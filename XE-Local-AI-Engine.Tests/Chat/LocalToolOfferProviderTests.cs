namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Marker I capability gate (Decision 7): the loopback offer list omits <c>run_in_agent_home</c> when the active
///     model id is not in <see cref="AgentHomeOptions.ToolCapableModels" />, and offers it when it is. Other catalog
///     tools are always offered. The encrypted path stays server-gated and never calls this provider.
/// </summary>
public sealed class LocalToolOfferProviderTests
{
    [Test]
    public void GetOfferedTools_WhenModelIsToolCapable_OffersAgentHomeTool()
    {
        var provider = CreateProvider("qwen3:8b");

        var offered = provider.GetOfferedTools("qwen3:8b");

        AssertEx.Contains(offered, tool => tool.Name == AgentHomeToolDefinition.ToolName);
        AssertEx.Contains(offered, tool => tool.Name == "open_url");
    }

    [Test]
    public void GetOfferedTools_WhenModelIsNotToolCapable_OmitsAgentHomeToolButKeepsOthers()
    {
        var provider = CreateProvider("qwen3:8b");

        var offered = provider.GetOfferedTools("some-other-model");

        AssertEx.False(offered.Any(tool => tool.Name == AgentHomeToolDefinition.ToolName),
            "run_in_agent_home must be withheld from a model that is not in ToolCapableModels");
        AssertEx.Contains(offered, tool => tool.Name == "open_url");
    }

    [Test]
    public void GetOfferedTools_WhenModelIsNull_OmitsAgentHomeTool()
    {
        var provider = CreateProvider("qwen3:8b");

        var offered = provider.GetOfferedTools(null);

        AssertEx.False(offered.Any(tool => tool.Name == AgentHomeToolDefinition.ToolName),
            "a null/unknown model is treated as not tool-capable, so the high-risk tool is withheld");
    }

    private static LocalToolOfferProvider CreateProvider(params string[] toolCapableModels)
    {
        var registry = new FakeAgentToolRegistry(
        [
            new LocalChatToolDescriptor(AgentHomeToolDefinition.ToolName, "Runs an agent task.", "{\"type\":\"object\"}", true),
            new LocalChatToolDescriptor("open_url", "Opens a URL.", "{\"type\":\"object\"}", false)
        ]);

        var options = Options.Create(new AgentHomeOptions { ToolCapableModels = toolCapableModels });
        return new LocalToolOfferProvider(registry, options);
    }

    private sealed class FakeAgentToolRegistry : IAgentToolRegistry
    {
        private readonly IReadOnlyList<LocalChatToolDescriptor> _descriptors;

        public FakeAgentToolRegistry(IReadOnlyList<LocalChatToolDescriptor> descriptors)
        {
            _descriptors = descriptors;
        }

        public IReadOnlyList<Microsoft.Extensions.AI.AITool> GetLocalChatTools()
        {
            return [];
        }

        public IReadOnlyList<LocalChatToolDescriptor> GetLocalChatToolDescriptors()
        {
            return _descriptors;
        }
    }
}
