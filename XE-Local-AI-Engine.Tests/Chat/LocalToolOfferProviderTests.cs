namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Capability gate (AgentHome Decision 7): the loopback offer omits <c>run_in_agent_home</c> and every MCP tool
///     when the active model is not in <see cref="AgentHomeOptions.ToolCapableModels" />, and offers them when it is.
///     Loop P4 extends this: the offer/known-name/known-tool surfaces merge the live MCP snapshot, MCP tools join the
///     capable-only set, and <c>GetKnownTools</c> tags each entry with its source.
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
    public void GetOfferedTools_WhenActiveModelEqualsToolCapableEntry_OffersAgentHomeTool()
    {
        // MED-4 regression: the live-evidence model id (qwen3:8b, the default ToolCapableModels entry) MUST satisfy
        // the gate when it is the offer-time active model — the bug was that this model never reached this seam, not
        // that the seam mismatched it. An exact match offers run_in_agent_home.
        var provider = CreateProvider("qwen3:8b");

        var offered = provider.GetOfferedTools("qwen3:8b");

        AssertEx.Contains(offered, tool => tool.Name == AgentHomeToolDefinition.ToolName);
    }

    [Test]
    public void GetOfferedTools_WhenActiveModelDiffersOnlyByCase_OmitsAgentHomeTool()
    {
        // The capability gate is intentionally an Ordinal (exact) match: a model id that differs only by case is NOT
        // tool-capable. This pins the matching contract so a future change cannot silently loosen it.
        var provider = CreateProvider("qwen3:8b");

        var offered = provider.GetOfferedTools("QWEN3:8B");

        AssertEx.False(offered.Any(tool => tool.Name == AgentHomeToolDefinition.ToolName),
            "the capability gate is an Ordinal exact match, so a case-only variant is not tool-capable");
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

    [Test]
    public void GetOfferedTools_WhenModelIsToolCapable_IncludesSnapshottedMcpTools()
    {
        var mcpRegistry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        mcpRegistry.ReplaceSnapshot([BuildMcpTool("mcp__weather__get_forecast")]);
        var provider = CreateProvider(mcpRegistry, "qwen3:8b");

        var offered = provider.GetOfferedTools("qwen3:8b");

        AssertEx.Contains(offered, tool => tool.Name == "mcp__weather__get_forecast");
    }

    [Test]
    public void GetOfferedTools_WhenModelIsNotToolCapable_WithholdsMcpTools()
    {
        var mcpRegistry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        mcpRegistry.ReplaceSnapshot([BuildMcpTool("mcp__weather__get_forecast")]);
        var provider = CreateProvider(mcpRegistry, "qwen3:8b");

        var offered = provider.GetOfferedTools("some-other-model");

        AssertEx.False(offered.Any(tool => tool.Name == "mcp__weather__get_forecast"),
            "MCP tools are capability-gated, so an incapable model is never offered them");
        AssertEx.Contains(offered, tool => tool.Name == "open_url");
    }

    [Test]
    public void GetKnownToolNames_IncludesBuiltinsAndSnapshottedMcpTools()
    {
        var mcpRegistry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        mcpRegistry.ReplaceSnapshot([BuildMcpTool("mcp__weather__get_forecast")]);
        var provider = CreateProvider(mcpRegistry, "qwen3:8b");

        var names = provider.GetKnownToolNames();

        AssertEx.Contains(names, AgentHomeToolDefinition.ToolName);
        AssertEx.Contains(names, "open_url");
        AssertEx.Contains(names, "mcp__weather__get_forecast");
    }

    [Test]
    public void GetKnownTools_TagsBuiltinAndMcpSourcesAndIgnoresCapabilityGating()
    {
        var mcpRegistry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        mcpRegistry.ReplaceSnapshot([BuildMcpTool("mcp__weather__get_forecast")]);
        var provider = CreateProvider(mcpRegistry, "qwen3:8b");

        var catalog = provider.GetKnownTools();

        var builtin = catalog.Single(entry => entry.Name == "open_url");
        AssertEx.Equal("builtin", builtin.Source);

        var agentHome = catalog.Single(entry => entry.Name == AgentHomeToolDefinition.ToolName);
        AssertEx.Equal("builtin", agentHome.Source);

        var mcp = catalog.Single(entry => entry.Name == "mcp__weather__get_forecast");
        AssertEx.Equal("mcp:weather", mcp.Source);
        AssertEx.True(mcp.RequiresApproval, "every MCP tool defaults to requiring approval");
        AssertEx.Equal("Gets the weather forecast.", mcp.Description);
    }

    [Test]
    public void GetKnownTools_WhenNoMcpServers_ReturnsBuiltinsOnly()
    {
        var provider = CreateProvider("qwen3:8b");

        var catalog = provider.GetKnownTools();

        AssertEx.True(catalog.All(entry => entry.Source == "builtin"),
            "with no MCP snapshot the catalog is built-ins only");
    }

    private static McpRegisteredTool BuildMcpTool(string qualifiedName)
    {
        var executable = AIFunctionFactory.Create((string input) => input, qualifiedName);
        var descriptor = new LocalChatToolDescriptor(qualifiedName, "Gets the weather forecast.", """{"type":"object"}""", true);
        return new McpRegisteredTool(qualifiedName, executable, descriptor);
    }

    private static LocalToolOfferProvider CreateProvider(params string[] toolCapableModels)
    {
        return CreateProvider(new McpToolRegistry(NullLogger<McpToolRegistry>.Instance), toolCapableModels);
    }

    private static LocalToolOfferProvider CreateProvider(IMcpToolRegistry mcpToolRegistry, params string[] toolCapableModels)
    {
        var registry = new FakeAgentToolRegistry(
        [
            new LocalChatToolDescriptor(AgentHomeToolDefinition.ToolName, "Runs an agent task.", "{\"type\":\"object\"}", true),
            new LocalChatToolDescriptor("open_url", "Opens a URL.", "{\"type\":\"object\"}", false)
        ]);

        var options = Options.Create(new AgentHomeOptions { ToolCapableModels = toolCapableModels });
        return new LocalToolOfferProvider(registry, mcpToolRegistry, options);
    }

    private sealed class FakeAgentToolRegistry : IAgentToolRegistry
    {
        private readonly IReadOnlyList<LocalChatToolDescriptor> _descriptors;

        public FakeAgentToolRegistry(IReadOnlyList<LocalChatToolDescriptor> descriptors)
        {
            _descriptors = descriptors;
        }

        public IReadOnlyList<AITool> GetLocalChatTools()
        {
            return [];
        }

        public IReadOnlyList<LocalChatToolDescriptor> GetLocalChatToolDescriptors()
        {
            return _descriptors;
        }
    }
}
