namespace XE_Local_AI_Engine.Tests.Coder;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Coder.Tools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The HIGH-1 offer-seam gates. <see cref="LocalToolOfferProvider" /> is built on the REAL
///     <see cref="LocalAgentToolRegistry" /> (which hardcodes GetCurrentTime + Calculate and does NOT project any
///     ClientLocal handler), so these tests fail if the §7.5 coder→offer merge regresses and the feature goes inert.
///     A resolution-seam test alone passes even when the offer seam is broken, so this is the load-bearing gate.
/// </summary>
public sealed class CoderOfferSeamTests
{
    private const string CapableModel = "qwen3:8b";

    private static readonly string[] CoderToolNames =
    [
        CoderToolDefinition.ListFilesToolName,
        CoderToolDefinition.ReadFileToolName,
        CoderToolDefinition.SearchTextToolName
    ];

    [Test]
    public void OfferSeam_CoderToolsAppearForCapableModel_WithRealRegistry()
    {
        var provider = CreateProvider(CapableModel);

        var offered = provider.GetOfferedTools(CapableModel);

        foreach (var toolName in CoderToolNames)
        {
            AssertEx.Contains(offered, tool => tool.Name == toolName);
        }
    }

    [Test]
    public void OfferSeam_CoderToolsAreAutoExecute_NotApprovalGated()
    {
        var provider = CreateProvider(CapableModel);

        var offered = provider.GetOfferedTools(CapableModel);

        foreach (var toolName in CoderToolNames)
        {
            var tool = offered.Single(candidate => candidate.Name == toolName);
            AssertEx.False(tool.RequiresApproval, "coder tools auto-run (decision 7)");
        }
    }

    [Test]
    public void OfferGate_CoderToolsWithheldFromIncapableModel()
    {
        var provider = CreateProvider(CapableModel);

        var offered = provider.GetOfferedTools("some-other-model");

        foreach (var toolName in CoderToolNames)
        {
            AssertEx.False(offered.Any(tool => tool.Name == toolName),
                "coder tools are capability-gated and must be withheld from a non-tool-capable model");
        }
    }

    [Test]
    public void OfferGate_CoderToolsWithheldFromNullModel()
    {
        var provider = CreateProvider(CapableModel);

        var offered = provider.GetOfferedTools(null);

        foreach (var toolName in CoderToolNames)
        {
            AssertEx.False(offered.Any(tool => tool.Name == toolName),
                "a null/unknown model is not tool-capable, so coder tools are withheld");
        }
    }

    [Test]
    public void KnownTools_IncludeCoderToolsUngated()
    {
        var provider = CreateProvider(CapableModel);

        var names = provider.GetKnownToolNames();

        foreach (var toolName in CoderToolNames)
        {
            AssertEx.Contains(names, toolName);
        }
    }

    [Test]
    public void KnownTools_SurfaceCoderToolsAsBuiltin_ForReactPickerAndCrudValidation()
    {
        // The CATALOG seam (GetKnownTools), not the offer seam, backs the React tool picker and agent-definition CRUD
        // "unknown tool name" validation. The three coder tools must appear here too — ungated by model — tagged
        // "builtin", or the seed's tool names would be flagged unknown and the picker would not list them.
        var provider = CreateProvider(CapableModel);

        var catalog = provider.GetKnownTools();

        foreach (var toolName in CoderToolNames)
        {
            var entry = catalog.Single(candidate => candidate.Name == toolName);
            AssertEx.Equal("builtin", entry.Source);
            AssertEx.False(entry.RequiresApproval, "coder tools auto-run (decision 7)");
        }
    }

    private static LocalToolOfferProvider CreateProvider(params string[] toolCapableModels)
    {
        // The REAL registry: BuildTools() hardcodes GetCurrentTime + Calculate. The coder tools reach the offer ONLY
        // through the §7.5 merge inside LocalToolOfferProvider, never through the registry.
        var registry = new LocalAgentToolRegistry();
        var mcpRegistry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        return new LocalToolOfferProvider(registry, mcpRegistry, toolCapableModels);
    }
}
