namespace XE_Local_AI_Engine.Tests.Integrations;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Integrations.Tools;
using XE_Local_AI_Engine.Tests.Testing;
using Harness = XE_Local_AI_Engine.Tests.Integrations.IntegrationCoordinatorHarness;

/// <summary>
///     Where <c>emit_output</c> is reachable from, and — far more of the surface — where it is not.
///     <para>
///         The protection is deliberately tripled: the node approval policy decides whether the call may execute
///         unattended at all, the OFFER decides who may be handed the tool, and the resolution chain decides whether a
///         call can land. Every non-integration caller fails the last two, and these are the tests that keep it so.
///     </para>
/// </summary>
public sealed class EmitOutputOfferTests
{
    private const string ToolName = EmitOutputToolDefinition.ToolName;

    private const string Model = IntegrationToolOfferFactory.CapableModel;

    [Test]
    public void EveryOfferAndCatalogSeam_OmitsEmitOutput()
    {
        // All eight seams on ILocalToolOfferProvider. The two async KNOWN-tool ones matter most: GetKnownToolsAsync is
        // what GET mcp/tool-catalog calls, so it is the seam that would put emit_output in the agent-editor's picker
        // and let an operator grant it per agent.
        var provider = IntegrationToolOfferFactory.Create();

        AssertEx.False(provider.GetOfferedTools(Model).Any(tool => tool.Name == ToolName), "GetOfferedTools is the plain chat offer.");
        AssertEx.False(provider.GetOfferedToolsForProfile(Model).Any(tool => tool.Name == ToolName),
            "The PROFILE pool is what an agent definition intersects against — including a spawned sub-agent's curated child tools, "
            + "so a child that names emit_output still cannot resolve it.");
        AssertEx.False(provider.GetKnownToolNames().Contains(ToolName, StringComparer.Ordinal), "GetKnownToolNames backs agent-definition validation.");
        AssertEx.False(provider.GetKnownTools().Any(entry => entry.Name == ToolName), "GetKnownTools backs the tool picker.");
    }

    [Test]
    public async Task EveryAsyncOfferAndCatalogSeam_OmitsEmitOutput()
    {
        var provider = IntegrationToolOfferFactory.Create();

        var offered = await provider.GetOfferedToolsAsync(Model, isCloudModel: false).ConfigureAwait(false);
        var profile = await provider.GetOfferedToolsForProfileAsync(Model, isCloudModel: false).ConfigureAwait(false);
        var names = await provider.GetKnownToolNamesAsync().ConfigureAwait(false);
        var catalog = await provider.GetKnownToolsAsync().ConfigureAwait(false);

        AssertEx.False(offered.Any(tool => tool.Name == ToolName));
        AssertEx.False(profile.Any(tool => tool.Name == ToolName));
        AssertEx.False(names.Contains(ToolName, StringComparer.Ordinal));
        AssertEx.False(catalog.Any(entry => entry.Name == ToolName), "GET mcp/tool-catalog reads this seam, and it is what the agent editor renders.");
    }

    [Test]
    public void GetIntegrationOutputOffer_ReturnsTheToolWithItsDeclaredFlags()
    {
        var provider = IntegrationToolOfferFactory.Create();

        var offer = provider.GetIntegrationOutputOffer();

        var tool = AssertEx.NotNull(offer.SingleOrDefault(candidate => candidate.Name == ToolName));
        AssertEx.Equal(ToolCategory.ReadLocal, tool.Category);
        AssertEx.False(tool.RequiresApproval, "The DECLARED flag. This provider consults no policy — the union recomposes it.");
        AssertEx.Equal(EmitOutputToolDefinition.ParameterSchema, tool.ParameterSchema, "The offer and the handler must advertise one schema.");
    }

    [Test]
    public async Task CoordinatorPackage_ContainsEmitOutput_EvenWhenTheAgentDoesNotListIt()
    {
        // The union is the whole delivery mechanism: no agent definition can grant this tool, and every integration run
        // gets it regardless of what its agent lists.
        using var harness = new Harness();

        await harness.Coordinator.ProcessOneAsync(harness.SeedAccepted(), CancellationToken.None);

        var package = harness.CapturedPackage ?? throw new AssertionException("The runner was never called.");
        AssertEx.Contains(package.AllowedTools, tool => tool.Name == ToolName);
    }

    [Test]
    public async Task WithNoNodePolicy_TheUnionedToolKeepsRequiresApprovalFalse()
    {
        // The identity case, so the compose cannot regress the default path: with no policy configured nothing about
        // today's behaviour changes.
        using var harness = new Harness();

        await harness.Coordinator.ProcessOneAsync(harness.SeedAccepted(), CancellationToken.None);

        var package = harness.CapturedPackage ?? throw new AssertionException("The runner was never called.");
        var tool = AssertEx.NotNull(package.AllowedTools.SingleOrDefault(candidate => candidate.Name == ToolName));
        AssertEx.False(tool.RequiresApproval);
    }

    [Test]
    public async Task WhenThePolicyTightensReadLocal_TheOfferedToolRequiresApproval()
    {
        // Fail-closed and deliberate: an operator who declares that ReadLocal tools need a human cannot be handed a
        // silent exception on the one surface reachable from outside the node. The tool is NOT stripped — it stays in
        // the package so the run fails audibly at the call rather than quietly completing without the capability.
        using var harness = new Harness
        {
            ToolApprovalPolicy = new TightenReadLocalPolicy()
        };

        await harness.Coordinator.ProcessOneAsync(harness.SeedAccepted(), CancellationToken.None);

        var package = harness.CapturedPackage ?? throw new AssertionException("The runner was never called.");
        var tool = AssertEx.NotNull(package.AllowedTools.SingleOrDefault(candidate => candidate.Name == ToolName));
        AssertEx.True(tool.RequiresApproval, "The union is the ONLY place this tool's flag can be composed, so a tightened ReadLocal must show up here.");
        AssertEx.True(package.IsUnattended, "And the run is unattended, which is what turns that flag into a refusal at the first call.");
    }

    /// <summary>A node whose operator requires approval for every read-only tool. Tighten-only, as the contract demands.</summary>
    private sealed class TightenReadLocalPolicy : IToolApprovalPolicy
    {
        public bool RequiresApproval(string toolName, ToolCategory category, bool catalogDefault) =>
            catalogDefault || category == ToolCategory.ReadLocal;
    }
}
