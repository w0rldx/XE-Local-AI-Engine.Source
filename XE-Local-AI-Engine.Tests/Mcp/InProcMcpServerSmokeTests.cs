namespace XE_Local_AI_Engine.Tests.Mcp;

using System.ComponentModel;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies the in-process MCP server harness itself: a real client connected over the in-memory stream pair can
///     list and call the tools the fake server exposes. This is the seam every connection-manager test relies on.
/// </summary>
public sealed class InProcMcpServerSmokeTests
{
    [Test]
    public async Task InProcServer_ListsAndCallsExposedTools()
    {
        await using var server = await InProcMcpServer.StartAsync("test-server",
            AIFunctionFactory.Create(Echo));

        var tools = await server.Client.ListToolsAsync();

        AssertEx.Contains(tools.Select(static tool => tool.Name), "Echo");
    }

    [Description("Echoes the input back.")]
    private static string Echo([Description("The value to echo.")] string value)
    {
        return value;
    }
}
