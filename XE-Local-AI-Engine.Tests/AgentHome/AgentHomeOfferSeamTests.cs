namespace XE_Local_AI_Engine.Tests.AgentHome;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The <c>run_in_agent_home</c> offer seam, built on the REAL <see cref="LocalAgentToolRegistry" /> (which hardcodes
///     GetCurrentTime + Calculate and projects no ClientLocal handler at all). Registering
///     <c>RunInAgentHomeToolHandler</c> in DI reaches the RESOLUTION seam only, so without the merge inside
///     <see cref="LocalToolOfferProvider" /> the tool is never offered to any model and the whole sandboxed write-back
///     subsystem is unreachable — which is exactly the state this class exists to prevent returning to. A test that
///     seeds the descriptor into a fake registry passes in that state, so only the real registry is load-bearing here.
///     <para>
///         The shape is <c>run_python</c>'s, not the coder tools': profile-opt-in only, never in the whole offer,
///         approval-required, and withheld outright from a model outside the trust boundary.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class AgentHomeOfferSeamTests
{
    private const string CapableModel = "qwen3:8b";

    [Test]
    public void OfferSeam_AgentHomeToolAppearsInTheProfilePool_WithRealRegistry()
    {
        var provider = CreateProvider(CapableModel);

        var pool = provider.GetOfferedToolsForProfile(CapableModel);

        var agentHome = AssertEx.NotNull(pool.FirstOrDefault(tool => tool.Name == AgentHomeToolDefinition.ToolName));
        AssertEx.True(agentHome.RequiresApproval,
            "run_in_agent_home must reach the model carrying its approval requirement — the handler hardcodes it and the node policy can only tighten.");
        AssertEx.Equal(ToolCategory.WriteExecute, agentHome.Category);
        AssertEx.Equal(ToolLocation.ClientLocal, agentHome.Location);
        AssertEx.Equal(AgentHomeToolDefinition.ParameterSchema, agentHome.ParameterSchema);
    }

    [Test]
    public async Task OfferSeam_AgentHomeToolAppearsInTheAsyncProfilePool_WithRealRegistry()
    {
        // The async pool is a separate code path (it folds in the custom-tool store read), so the merge is pinned on
        // both or one of them can drift shut.
        var provider = CreateProvider(CapableModel);

        var pool = await provider.GetOfferedToolsForProfileAsync(CapableModel, isCloudModel: false);

        AssertEx.Contains(pool, tool => tool.Name == AgentHomeToolDefinition.ToolName);
    }

    [Test]
    public void OfferGate_AgentHomeToolIsNeverInTheWholeOffer()
    {
        // The default / mode-off chat path. Holding the tool out keeps the deepest schema we ship out of the GBNF
        // grammar llama.cpp compiles on an ordinary turn, and keeps a plain chat from directing sandbox execution.
        var provider = CreateProvider(CapableModel);

        AssertEx.False(provider.GetOfferedTools(CapableModel).Any(tool => tool.Name == AgentHomeToolDefinition.ToolName),
            "run_in_agent_home must NOT be in the default/mode-off whole offer");
    }

    [Test]
    public void OfferGate_AgentHomeToolWithheldFromIncapableModel()
    {
        var provider = CreateProvider(CapableModel);

        AssertEx.False(provider.GetOfferedToolsForProfile("some-other-model").Any(tool => tool.Name == AgentHomeToolDefinition.ToolName),
            "run_in_agent_home is capability-gated, so a profile opt-in cannot bypass the gate");
    }

    [Test]
    public void OfferGate_AgentHomeToolWithheldFromNullModel()
    {
        var provider = CreateProvider(CapableModel);

        AssertEx.False(provider.GetOfferedToolsForProfile(null).Any(tool => tool.Name == AgentHomeToolDefinition.ToolName),
            "a null/unknown model is not tool-capable, so run_in_agent_home is withheld");
    }

    [Test]
    public void OfferGate_AgentHomeToolWithheldFromACloudModel()
    {
        // Unconditional, not behind AllowCloudModelAccess: what is withheld is a REMOTE model's ability to run commands
        // and write files in a node-local sandbox, which that opt-in cannot be given informedly about.
        var provider = CreateProvider(CapableModel);

        AssertEx.False(provider.GetOfferedToolsForProfile(CapableModel, isCloudModel: true).Any(tool => tool.Name == AgentHomeToolDefinition.ToolName),
            "run_in_agent_home must never be offered to a cloud-hosted model");
    }

    [Test]
    public async Task OfferGate_AgentHomeToolWithheldFromACloudModel_OnTheAsyncPoolToo()
    {
        var provider = CreateProvider(CapableModel);

        AssertEx.False((await provider.GetOfferedToolsForProfileAsync(CapableModel, isCloudModel: true))
                       .Any(tool => tool.Name == AgentHomeToolDefinition.ToolName),
            "run_in_agent_home must never be offered to a cloud-hosted model on the async pool either");
    }

    [Test]
    public void OfferGate_AgentHomeToolWithheldFromADeclaredCloudExternalEndpoint()
    {
        // The turn's own flag says local — this is an agent PINNED to an external id — so the declared locality is the
        // only thing that can catch it, exactly as it is for run_python.
        const string modelId = "ext:hosted-box/qwen3";
        var provider = CreateProvider(new FakeModelTrustResolver().Register("hosted-box", "qwen3", ExternalProviderLocality.Cloud), modelId);

        AssertEx.False(provider.GetOfferedToolsForProfile(modelId, isCloudModel: false).Any(tool => tool.Name == AgentHomeToolDefinition.ToolName),
            "a declared-cloud external model must not be offered run_in_agent_home");
    }

    [Test]
    public void OfferGate_AgentHomeToolWithheldWhenTheExternalRegistrationCannotBeResolved()
    {
        const string modelId = "ext:hosted-box/qwen3";
        var provider = CreateProvider(new FakeModelTrustResolver
            {
                CacheIsCold = true
            },
            modelId);

        AssertEx.False(provider.GetOfferedToolsForProfile(modelId, isCloudModel: false).Any(tool => tool.Name == AgentHomeToolDefinition.ToolName),
            "an unresolved external model must not be offered run_in_agent_home — only a positively resolved local declaration earns local privileges");
    }

    [Test]
    public void KnownTools_IncludeAgentHomeToolUngated_ForTheReactPickerAndCrudValidation()
    {
        // The CATALOG seam, not the offer seam, backs the agent editor's tool picker and agent-definition CRUD
        // "unknown tool name" validation. Listing it here ungated is the ONLY way an operator can opt an agent in —
        // and the opt-in is the only way the tool is ever offered at all.
        var provider = CreateProvider(CapableModel);

        AssertEx.Contains(provider.GetKnownToolNames(), AgentHomeToolDefinition.ToolName);

        var entry = provider.GetKnownTools().Single(candidate => candidate.Name == AgentHomeToolDefinition.ToolName);
        AssertEx.Equal("builtin", entry.Source);
        AssertEx.Equal(AgentHomeToolDefinition.Description, entry.Description);
        AssertEx.True(entry.RequiresApproval);
        AssertEx.Equal(ToolCategory.WriteExecute, entry.Category);
    }

    [Test]
    public void OfferSeam_AgentHomeDescriptorIsBuiltOnceAndIsIdenticalAcrossSends()
    {
        // The offer must be byte-identical across sends or the per-turn config hash changes and the provider re-primes.
        // Building the descriptor per call would break that silently, so pin REFERENCE equality: the DTO is constructed
        // at construction time and handed out, never re-allocated.
        var provider = CreateProvider(CapableModel);

        var first = provider.GetOfferedToolsForProfile(CapableModel).Single(tool => tool.Name == AgentHomeToolDefinition.ToolName);
        var second = provider.GetOfferedToolsForProfile(CapableModel).Single(tool => tool.Name == AgentHomeToolDefinition.ToolName);

        AssertEx.True(ReferenceEquals(first, second), "the AgentHome offer DTO must be built once at construction, not per call");
    }

    [Test]
    public void UnattendedGate_AnOpaqueWorkspaceBindingCarryingAgentHome_IsRejected()
    {
        // Delegate-scope inbound MCP is one of the unattended callers. Its opaque-workspace binding is fail-closed on
        // BOTH axes — an exact read-only coder name set AND no approval-required tool — so the real offer DTO must fail
        // it. This keys off the descriptor, not a denylist, which is why adding a tool cannot quietly widen the seam.
        var provider = CreateProvider(CapableModel);
        var agentHome = provider.GetOfferedToolsForProfile(CapableModel).Single(tool => tool.Name == AgentHomeToolDefinition.ToolName);

        var binding = new McpExecutionBinding
        {
            BindingFingerprint = "fp",
            ModelId = CapableModel,
            Instructions = "child",
            AgentDefinitionId = Guid.NewGuid(),
            AgentDefinitionVersion = 1,
            AllowedTools = [agentHome],
            ReasoningEffort = null,
            SupportsThinking = false
        };

        AssertEx.False(McpExecutionBindingPolicy.IsExactReadOnlyWorkspaceCoder(binding),
            "an inbound binding offering run_in_agent_home must never be handed an opaque workspace");
    }

    private static LocalToolOfferProvider CreateProvider(params string[] toolCapableModels)
    {
        return CreateProvider(new FakeModelTrustResolver(), toolCapableModels);
    }

    private static LocalToolOfferProvider CreateProvider(FakeModelTrustResolver trustResolver, params string[] toolCapableModels)
    {
        // The REAL registry: its constructor hardcodes GetCurrentTime + Calculate and knows nothing about AgentHome.
        // run_in_agent_home reaches the offer ONLY through the merge inside LocalToolOfferProvider.
        return new LocalToolOfferProvider(new LocalAgentToolRegistry(TimeProvider.System),
            new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
            StubNodeRuntimeSettings.Create().WithToolCapableModels(toolCapableModels).Build(),
            NullCustomToolScopeFactory.Instance,
            trustResolver,
            allowCloudKnowledgeAccess: false);
    }
}
