namespace XE_Local_AI_Engine.Tests.Agents;

using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The always-on tool names a relevance filter may never hide. The set is deliberately narrow: the four
///     work-session state tools plus every approval-bearing BUILT-IN. MCP and custom tools are ranked like anything
///     else — they are exactly the tools that push a real agent past the threshold, and hiding one never bypasses the
///     approval it carries from registry build.
/// </summary>
public sealed class ToolRelevanceCoreSetTests
{
    [Test]
    public void GetCoreToolNames_ContainsEveryWorkSessionTool()
    {
        var core = BuildCoreSet().GetCoreToolNames();

        foreach (var name in WorkSessionToolDefinitions.ToolNames)
        {
            AssertEx.Contains(core, name, $"A work-session state tool must always be offered: {name}.");
        }
    }

    [Test]
    public void GetCoreToolNames_ContainsEveryApprovalBearingBuiltin()
    {
        var core = BuildCoreSet().GetCoreToolNames();

        AssertEx.Contains(core, "run_python", "An approval-bearing built-in is core.");
    }

    [Test]
    public void GetCoreToolNames_OmitsABuiltinThatCarriesNoApproval()
    {
        var core = BuildCoreSet().GetCoreToolNames();

        AssertEx.False(core.Contains("get_time"), "A built-in with no approval is ranked like any other non-core tool.");
    }

    [Test]
    public void GetCoreToolNames_OmitsMcpAndCustomTools()
    {
        var core = BuildCoreSet().GetCoreToolNames();

        AssertEx.False(core.Contains("mcp_write_file"), "An MCP tool is ranked, never core — the amended D6 ruling.");
        AssertEx.False(core.Contains("custom__deploy"), "A custom tool is ranked, never core — the amended D6 ruling.");
    }

    private static ToolRelevanceCoreSet BuildCoreSet()
    {
        var offerProvider = Substitute.For<ILocalToolOfferProvider>();
        offerProvider.GetKnownTools().Returns(
        [
            Entry("run_python", requiresApproval: true, source: "builtin"),
            Entry("get_time", requiresApproval: false, source: "builtin"),
            // Approval-bearing, but not a built-in: the Source tag, not the approval flag, is what keeps these out.
            Entry("mcp_write_file", requiresApproval: true, source: "mcp:files"),
            Entry("custom__deploy", requiresApproval: true, source: "custom")
        ]);

        return new ToolRelevanceCoreSet(offerProvider);
    }

    private static LocalToolCatalogEntry Entry(string name, bool requiresApproval, string source)
    {
        return new LocalToolCatalogEntry
        {
            Name = name,
            Description = $"The {name} tool.",
            RequiresApproval = requiresApproval,
            Source = source
        };
    }
}
