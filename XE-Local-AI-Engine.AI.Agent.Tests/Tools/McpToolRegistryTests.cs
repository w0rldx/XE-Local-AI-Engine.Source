namespace XE_Local_AI_Engine.AI.Agent.Tests.Tools;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class McpToolRegistryTests
{
    [Test]
    public void ReplaceSnapshot_PublishesExecutablesAndDescriptors()
    {
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);

        registry.ReplaceSnapshot([Tool("mcp__a__one"), Tool("mcp__a__two")]);

        AssertEx.Equal(2, registry.GetDescriptors().Count);
        AssertEx.True(registry.TryResolve("mcp__a__one", out _));
        AssertEx.True(registry.TryResolve("mcp__a__two", out _));
    }

    [Test]
    public void ReplaceSnapshot_OnDuplicateName_KeepsFirstAndDropsDescriptorInLockstep()
    {
        // LOW-1: a duplicate qualified name must not leave the descriptor list (offer) advertising a tool whose
        // executable was overwritten. First write wins for BOTH the executable and the descriptor, so descriptor count
        // equals executable count.
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        var first = Tool("mcp__a__dup", "first");
        var second = Tool("mcp__a__dup", "second");

        registry.ReplaceSnapshot([first, second]);

        var descriptors = registry.GetDescriptors();
        AssertEx.Equal(1, descriptors.Count);
        AssertEx.Equal("first", descriptors[0].Description);
        AssertEx.True(registry.TryResolve("mcp__a__dup", out var executable));
        AssertEx.Equal(first.Executable, executable);
    }

    [Test]
    public void ReplaceSnapshot_ReplacesPriorSnapshotWholesale()
    {
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        registry.ReplaceSnapshot([Tool("mcp__a__one")]);

        registry.ReplaceSnapshot([Tool("mcp__b__two")]);

        AssertEx.False(registry.TryResolve("mcp__a__one", out _));
        AssertEx.True(registry.TryResolve("mcp__b__two", out _));
    }

    private static McpRegisteredTool Tool(string name, string description = "desc")
    {
        var executable = AIFunctionFactory.Create((string input) => input, name);
        var descriptor = new LocalChatToolDescriptor(name, description, """{"type":"object"}""", true);
        return new McpRegisteredTool(name, executable, descriptor);
    }
}
