namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Instructions;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class AgentDefinitionResolverTests
{
    private const string SystemPrompt = "You are the bound persona.";

    // The tool-capable model id and the capability-gated tool name the real LocalToolOfferProvider gates on
    // (AgentHomeOptions.ToolCapableModels default + AgentHomeToolDefinition.ToolName); the stub mirrors that gating.
    private const string ToolCapableModel = "qwen3:8b";
    private const string CapabilityGatedToolName = "run_in_agent_home";
    private const string IncapableModel = "tiny:1b";

    [Test]
    public async Task ResolveAsync_WhenAgentDefinitionIdIsNull_ReturnsNull()
    {
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"));

        var resolved = await resolver.ResolveAsync(agentDefinitionId: null, "qwen3:8b").ConfigureAwait(false);

        AssertEx.True(resolved is null, "A null binding must resolve to null (default persona).");
        await store.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ResolveAsync_WhenDefinitionMissing_ReturnsNull()
    {
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"));
        var missingId = Guid.NewGuid();
        store.GetByIdAsync(missingId, Arg.Any<CancellationToken>()).Returns((AgentDefinitionRecord?)null);

        var resolved = await resolver.ResolveAsync(missingId, "qwen3:8b").ConfigureAwait(false);

        AssertEx.True(resolved is null, "A binding to a deleted definition must resolve to null (default persona).");
    }

    [Test]
    public async Task ResolveAsync_WhenBound_ProjectsInstructionsModelReasoningAndVersion()
    {
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: ["GetCurrentTime"], version: 4, modelProfile: "qwen3:8b", reasoningEffort: "high");
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolved, "A bound definition must resolve to a runtime projection.");
        AssertEx.Equal(SystemPrompt, resolved!.ResolvedSystemPrompt);
        AssertEx.Equal("qwen3:8b", resolved.ModelProfile);
        AssertEx.Equal("high", resolved.ReasoningEffort);
        AssertEx.Equal(expected: 4, resolved.AgentDefinitionVersion);
    }

    [Test]
    public async Task ResolveAsync_ReturnsAgentIdAndName()
    {
        // The runtime projection carries the resolved agent's id + display-name snapshot so the stream service can stamp
        // per-response attribution without a second fetch.
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"));
        var definition = CreateDefinition("Backend Buddy", allowedTools: ["GetCurrentTime"]);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Equal(definition.Id, resolved!.AgentDefinitionId);
        AssertEx.Equal("Backend Buddy", resolved.AgentName);
    }

    [Test]
    public async Task ResolveAsync_WhenDefaultAssistantSlug_UsesFullToolOffer()
    {
        // The seeded Default Assistant (mode-off persona) gets the FULL capability-gated offer, NOT the intersected
        // allowed set — even though its AllowedToolNames is empty, both offered tools survive (reproduces today's chat).
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"), OfferTool("Calculate"));
        var defaultAssistant = CreateDefinition(AgentDefaults.DefaultAgentName, allowedTools: []) with
        {
            Source = AgentDefinitionSource.Seeded,
            SeedSlug = AgentDefaults.DefaultAgentSeedSlug
        };
        store.GetByIdAsync(defaultAssistant.Id, Arg.Any<CancellationToken>()).Returns(defaultAssistant);

        var resolved = await resolver.ResolveAsync(defaultAssistant.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Equal(expected: 2, resolved!.AllowedTools.Count);
        AssertEx.Contains(resolved.AllowedTools, tool => tool.Name == "GetCurrentTime");
        AssertEx.Contains(resolved.AllowedTools, tool => tool.Name == "Calculate");
    }

    [Test]
    public async Task ResolveAsync_DefaultAssistant_AppliesNodeApprovalPolicyToWholeOffer()
    {
        // The mode-off Default Assistant takes the whole offer, and the node policy tightens it too — a node-wide policy
        // is not bypassable by plain mode-off chat. Node policy: require approval for the ReadLocal category.
        var nodePolicy = new NodeToolApprovalPolicy(
            new Dictionary<ToolCategory, bool> { [ToolCategory.ReadLocal] = true },
            new Dictionary<string, bool>(StringComparer.Ordinal));
        var resolver = CreateResolverWithPolicy(out var store,
            nodePolicy,
            OfferTool("GetCurrentTime", category: ToolCategory.ReadLocal),
            OfferTool("Calculate", category: ToolCategory.ReadLocal));
        var defaultAssistant = CreateDefinition(AgentDefaults.DefaultAgentName, allowedTools: []) with
        {
            Source = AgentDefinitionSource.Seeded,
            SeedSlug = AgentDefaults.DefaultAgentSeedSlug
        };
        store.GetByIdAsync(defaultAssistant.Id, Arg.Any<CancellationToken>()).Returns(defaultAssistant);

        var resolved = await resolver.ResolveAsync(defaultAssistant.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Equal(expected: 2, resolved!.AllowedTools.Count); // whole offer preserved
        AssertEx.True(resolved.AllowedTools.All(tool => tool.RequiresApproval),
            "A node ReadLocal tightening must apply to the mode-off Default Assistant's whole offer.");
    }

    [Test]
    public async Task ResolveAsync_DefaultAssistant_WhenNoNodePolicy_OfferAndConfigHashByteIdentical()
    {
        // Identity floor: with the Permissive (no node policy) floor, the Default Assistant's whole offer is unchanged —
        // each tool keeps its own catalog RequiresApproval AND the runtime-package config hash matches a package built
        // from the raw offer. Proves the mode-off path is byte-identical to the pre-OPP-03 path when unconfigured.
        var builder = new LocalChatRuntimePackageBuilder();
        var mcp = OfferTool("mcp__x__y", requiresApproval: true, ToolCategory.Network);
        var clock = OfferTool("GetCurrentTime", category: ToolCategory.ReadLocal);
        var resolver = CreateResolver(out var store, mcp, clock);
        var defaultAssistant = CreateDefinition(AgentDefaults.DefaultAgentName, allowedTools: []) with
        {
            Source = AgentDefinitionSource.Seeded,
            SeedSlug = AgentDefaults.DefaultAgentSeedSlug
        };
        store.GetByIdAsync(defaultAssistant.Id, Arg.Any<CancellationToken>()).Returns(defaultAssistant);

        var resolved = await resolver.ResolveAsync(defaultAssistant.Id, ToolCapableModel).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        // Flags are identity: MCP stays true, the read-only clock stays false — no node tightening.
        AssertEx.Equal(expected: true, resolved!.AllowedTools.Single(tool => tool.Name == "mcp__x__y").RequiresApproval);
        AssertEx.Equal(expected: false, resolved.AllowedTools.Single(tool => tool.Name == "GetCurrentTime").RequiresApproval);

        var resolvedPackage = builder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            Guid.NewGuid(),
            resolved.ResolvedSystemPrompt,
            [],
            resolved.ModelProfile,
            resolved.AgentDefinitionVersion,
            AllowedTools: resolved.AllowedTools,
            ReasoningEffort: resolved.ReasoningEffort));
        var rawOfferPackage = builder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            Guid.NewGuid(),
            resolved.ResolvedSystemPrompt,
            [],
            resolved.ModelProfile,
            resolved.AgentDefinitionVersion,
            AllowedTools: [mcp, clock],
            ReasoningEffort: resolved.ReasoningEffort));
        AssertEx.Equal(rawOfferPackage.ConfigHash, resolvedPackage.ConfigHash);
    }

    [Test]
    public async Task ResolveAsync_WhenDefaultAssistant_DoesNotGetSpawnSubAgent()
    {
        // spawn_subagent is profile-opt-in only: even though it is offered to a tool-capable model, the mode-off Default
        // Assistant takes the WHOLE offer (which excludes spawn_subagent), so a plain chat turn never gets it.
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"), OfferTool("spawn_subagent"));
        var defaultAssistant = CreateDefinition(AgentDefaults.DefaultAgentName, allowedTools: []) with
        {
            Source = AgentDefinitionSource.Seeded,
            SeedSlug = AgentDefaults.DefaultAgentSeedSlug
        };
        store.GetByIdAsync(defaultAssistant.Id, Arg.Any<CancellationToken>()).Returns(defaultAssistant);

        var resolved = await resolver.ResolveAsync(defaultAssistant.Id, ToolCapableModel).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.False(resolved!.AllowedTools.Any(tool => tool.Name == "spawn_subagent"),
            "the Default Assistant / mode-off offer must never contain spawn_subagent");
    }

    [Test]
    public async Task ResolveAsync_WhenProfileAllowsSpawnSubAgent_OnCapableModel_GetsIt()
    {
        // An explicit profile that lists spawn_subagent in AllowedToolNames on a tool-capable model resolves it (the
        // intersection uses the profile pool, which includes spawn_subagent).
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"), OfferTool("spawn_subagent"));
        var definition = CreateDefinition(allowedTools: ["spawn_subagent"], modelProfile: ToolCapableModel);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, ToolCapableModel).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Contains(resolved!.AllowedTools, tool => tool.Name == "spawn_subagent");
    }

    [Test]
    public async Task ResolveAsync_WhenProfileDoesNotAllowSpawnSubAgent_DoesNotGetIt()
    {
        // A profile that does NOT list spawn_subagent never receives it, even though the pool offers it.
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"), OfferTool("spawn_subagent"));
        var definition = CreateDefinition(allowedTools: ["GetCurrentTime"], modelProfile: ToolCapableModel);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, ToolCapableModel).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.False(resolved!.AllowedTools.Any(tool => tool.Name == "spawn_subagent"),
            "a profile that does not allow spawn_subagent must not receive it");
        AssertEx.Contains(resolved.AllowedTools, tool => tool.Name == "GetCurrentTime");
    }

    [Test]
    public async Task Resolver_ResolvesEnabledAssignedSkills_DropsDisabledAndMissing()
    {
        // The picklist names three skills; the store's enabled-by-ids fast path returns only the one that is still
        // present AND enabled. The resolver must surface exactly that one, dropping the deleted and disabled ids (the
        // store already omits them) without fabricating anything.
        var resolver = CreateResolverWithSkills(out var store, out var skillStore);
        var enabledId = Guid.NewGuid();
        var disabledId = Guid.NewGuid();
        var deletedId = Guid.NewGuid();
        var definition = CreateDefinition(allowedSkillIds: [enabledId, disabledId, deletedId]);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);
        skillStore.ListEnabledByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<IReadOnlyList<AgentSkillRecord>>([SkillRecord(enabledId, "kubernetes-debug", "Debug k8s issues", "## Body", version: 3)]));

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.NotNull(resolved!.Skills);
        AssertEx.Equal(expected: 1, resolved.Skills!.Count);
        var skill = resolved.Skills[0];
        AssertEx.Equal(enabledId, skill.Id);
        AssertEx.Equal("kubernetes-debug", skill.Name);
        AssertEx.Equal("Debug k8s issues", skill.Description);
        AssertEx.Equal("## Body", skill.Body);
        AssertEx.Equal(expected: 3, skill.Version);
    }

    [Test]
    public async Task Resolver_WhenNoSkillsAssigned_SkipsStoreAndResolvesEmptySkillSet()
    {
        // The empty-picklist short-circuit: the resolver returns an empty skill set without ever calling the store, so
        // the no-skills resolve stays byte-identical to the pre-skills path.
        var resolver = CreateResolverWithSkills(out var store, out var skillStore);
        var definition = CreateDefinition(allowedSkillIds: []);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.NotNull(resolved!.Skills);
        AssertEx.Equal(expected: 0, resolved.Skills!.Count);
        await skillStore.DidNotReceive().ListEnabledByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ResolveAsync_WhenSeededSlugButNotDefaultAssistant_StaysIntersected()
    {
        // The full-offer branch is keyed strictly on the default-assistant slug: any other seeded row stays intersected,
        // so a starter-pack persona can never claim the full offer.
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"), OfferTool("Calculate"));
        var seededPersona = CreateDefinition("Seeded persona", allowedTools: ["GetCurrentTime"]) with
        {
            Source = AgentDefinitionSource.Seeded,
            SeedSlug = "some-other-pack-agent"
        };
        store.GetByIdAsync(seededPersona.Id, Arg.Any<CancellationToken>()).Returns(seededPersona);

        var resolved = await resolver.ResolveAsync(seededPersona.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Equal(expected: 1, resolved!.AllowedTools.Count);
        AssertEx.Equal("GetCurrentTime", resolved.AllowedTools[0].Name);
    }

    [Test]
    public async Task ResolveAsync_IntersectsOfferToAllowedToolNames_AndDropsUnknown()
    {
        // The offer has two tools; the definition allows one offered tool plus one that is not in the offer. Only the
        // intersection survives; the unknown name is dropped, never fabricated.
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"), OfferTool("Calculate"));
        var definition = CreateDefinition(allowedTools: ["Calculate", "NotOffered"]);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Equal(expected: 1, resolved!.AllowedTools.Count);
        AssertEx.Equal("Calculate", resolved.AllowedTools[0].Name);
    }

    // ---- R1: knowledge-tool locality gates on the EFFECTIVE (post-pin) model, not the turn's active model ----

    private const string KnowledgeSearchToolName = "search_knowledge_base";
    private const string CloudPinnedModel = "azure-foundry-deploy";

    [Test]
    public async Task ResolveAsync_WhenAgentPinnedToCloudModel_OnLocalActiveTurn_WithholdsKnowledgeToolsByDefault()
    {
        // R1 (HIGH): the turn's active model is LOCAL, but the agent pins a CLOUD model. The knowledge tools must be
        // gated on the pinned effective model's locality — otherwise a cloud-pinned agent leaks node-local documents.
        var capabilityResolver = Substitute.For<IModelCapabilityResolver>();
        capabilityResolver.ResolveAsync(CloudPinnedModel, Arg.Any<CancellationToken>())
                          .Returns(Task.FromResult((SupportsThinking: false, SupportsTools: true, IsCloud: true)));

        var resolver = BuildRealOfferResolver(out var store, allowCloudKnowledgeAccess: false, capabilityResolver);
        var definition = CreateDefinition(allowedTools: [KnowledgeSearchToolName, "GetCurrentTime"], modelProfile: CloudPinnedModel);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        // Active model local (activeModelIsCloud: false); pin is cloud.
        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b", retrievalQuery: null, supportsTools: true, honorModelProfile: true, activeModelIsCloud: false)
                                     .ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.False(resolved!.AllowedTools.Any(tool => tool.Name == KnowledgeSearchToolName),
            "a cloud-PINNED agent must not be offered the knowledge tools even when the turn's active model is local");
    }

    [Test]
    public async Task ResolveAsync_WhenAgentPinnedToCloudModel_AndOperatorOptedIn_OffersKnowledgeTools()
    {
        var capabilityResolver = Substitute.For<IModelCapabilityResolver>();
        capabilityResolver.ResolveAsync(CloudPinnedModel, Arg.Any<CancellationToken>())
                          .Returns(Task.FromResult((SupportsThinking: false, SupportsTools: true, IsCloud: true)));

        var resolver = BuildRealOfferResolver(out var store, allowCloudKnowledgeAccess: true, capabilityResolver);
        var definition = CreateDefinition(allowedTools: [KnowledgeSearchToolName, "GetCurrentTime"], modelProfile: CloudPinnedModel);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b", retrievalQuery: null, supportsTools: true, honorModelProfile: true, activeModelIsCloud: false)
                                     .ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.True(resolved!.AllowedTools.Any(tool => tool.Name == KnowledgeSearchToolName),
            "the opt-in (KnowledgeBase:AllowCloudModelAccess=true) restores knowledge tools for a cloud-pinned agent");
    }

    [Test]
    public async Task ResolveAsync_AppliesToolApprovalOverrides_FallingBackToDescriptorFlag()
    {
        // The offer ships both tools as non-approval. The definition overrides one to require approval and leaves the
        // other to its descriptor default.
        var resolver = CreateResolver(out var store,
            OfferTool("GetCurrentTime"),
            OfferTool("Calculate"));
        var definition = CreateDefinition(allowedTools: ["GetCurrentTime", "Calculate"],
            toolApprovals: new Dictionary<string, bool>
            {
                ["GetCurrentTime"] = true
            });
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        var gated = resolved!.AllowedTools.Single(tool => tool.Name == "GetCurrentTime");
        var ungated = resolved.AllowedTools.Single(tool => tool.Name == "Calculate");
        AssertEx.Equal(expected: true, gated.RequiresApproval);
        AssertEx.Equal(expected: false, ungated.RequiresApproval);
    }

    [Test]
    public async Task ResolveAsync_ProjectsOfferedMcpTool_AndPerAgentAutoExecuteCannotLoosenApproval()
    {
        // MCP tool projection under TIGHTEN-ONLY (OPP-03): an MCP tool is in the offer approval-ON by default (its
        // catalog RequiresApproval=true). A per-agent ToolApprovals[mcpTool]=false can NO LONGER loosen it — the 3-tier
        // compose is `nodePolicy(catalogDefault) || (perAgent && perAgentValue)`, so a per-agent false is a no-op and the
        // resolved offer flag stays true. (Before OPP-03 this test asserted the flag flipped to false. The MCP executable
        // was ALWAYS wrapped in ApprovalRequiredAIFunction at McpServerConnectionManager regardless of this flag, so no
        // real execution loosening is lost; the flag now correctly matches that structural floor.)
        const string mcpTool = "mcp__weather__get_forecast";
        var resolver = CreateResolver(out var store,
            OfferTool(mcpTool, requiresApproval: true),
            OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: [mcpTool, "GetCurrentTime"],
            modelProfile: ToolCapableModel,
            toolApprovals: new Dictionary<string, bool>
            {
                [mcpTool] = false
            });
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, ToolCapableModel).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        var projected = resolved!.AllowedTools.Single(tool => tool.Name == mcpTool);
        AssertEx.Equal(expected: true, projected.RequiresApproval);
    }

    [Test]
    public async Task ResolveAsync_AppliesThreeTierTightenOnlyApprovalCompose()
    {
        // End-to-end seam D (OPP-03): the node policy tightens a whole category, then the per-agent override can only ADD
        // approval on top of it. Node policy: require approval for the Network category. Definition: names three tools and
        // tightens GetCurrentTime while trying (and failing) to auto-execute the Network tool.
        var nodePolicy = new NodeToolApprovalPolicy(
            new Dictionary<ToolCategory, bool> { [ToolCategory.Network] = true },
            new Dictionary<string, bool>(StringComparer.Ordinal));
        var resolver = CreateResolverWithPolicy(out var store,
            nodePolicy,
            OfferTool("mcp__x__y", requiresApproval: false, ToolCategory.Network),
            OfferTool("GetCurrentTime", category: ToolCategory.ReadLocal),
            OfferTool("Calculate", category: ToolCategory.ReadLocal));
        var definition = CreateDefinition(allowedTools: ["mcp__x__y", "GetCurrentTime", "Calculate"],
            modelProfile: ToolCapableModel,
            toolApprovals: new Dictionary<string, bool>
            {
                ["GetCurrentTime"] = true, // per-agent tighten a node-auto-execute tool
                ["mcp__x__y"] = false       // per-agent false must NOT loosen the node-tightened tool
            });
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, ToolCapableModel).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        // Network tool: tightened by the node category rule; the per-agent false is a no-op.
        AssertEx.Equal(expected: true, resolved!.AllowedTools.Single(tool => tool.Name == "mcp__x__y").RequiresApproval);
        // ReadLocal tool the node policy leaves alone, but the per-agent override tightens.
        AssertEx.Equal(expected: true, resolved.AllowedTools.Single(tool => tool.Name == "GetCurrentTime").RequiresApproval);
        // ReadLocal tool with neither a node rule nor a per-agent override stays auto-execute.
        AssertEx.Equal(expected: false, resolved.AllowedTools.Single(tool => tool.Name == "Calculate").RequiresApproval);
    }

    [Test]
    public async Task ResolveAsync_WhenPinnedModelNotToolCapable_DropsCapabilityGatedTool()
    {
        // The definition pins a NON-tool-capable model and names the capability-gated tool. The resolver gates the
        // offer by the pinned (effective) model, so the high-risk tool is withheld and only the safe tool survives.
        var resolver = CreateResolver(out var store,
            OfferTool(CapabilityGatedToolName),
            OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: [CapabilityGatedToolName, "GetCurrentTime"], modelProfile: IncapableModel);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, ToolCapableModel).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.False(resolved!.AllowedTools.Any(tool => tool.Name == CapabilityGatedToolName),
            "A non-tool-capable pinned model must not be offered the capability-gated tool.");
        AssertEx.Contains(resolved.AllowedTools, tool => tool.Name == "GetCurrentTime");
    }

    [Test]
    public async Task ResolveAsync_WhenPinnedModelToolCapable_KeepsCapabilityGatedTool()
    {
        // Same definition, but the pinned model IS tool-capable. The effective-model gating now offers the high-risk
        // tool, so it survives the intersection.
        var resolver = CreateResolver(out var store,
            OfferTool(CapabilityGatedToolName),
            OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: [CapabilityGatedToolName, "GetCurrentTime"], modelProfile: ToolCapableModel);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        // The caller's active model is the incapable one; the pinned tool-capable model must win the gating decision.
        var resolved = await resolver.ResolveAsync(definition.Id, IncapableModel).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Contains(resolved!.AllowedTools, tool => tool.Name == CapabilityGatedToolName);
        AssertEx.Contains(resolved.AllowedTools, tool => tool.Name == "GetCurrentTime");
    }

    [Test]
    public async Task ResolveAsync_WhenModelLacksToolsCapability_WithholdsAllTools()
    {
        // The model-capability gate (supportsTools=false) overrides everything: even a tool-capable model name and a
        // definition that names safe tools yields an EMPTY offer, because the active model cannot drive any tool call.
        var resolver = CreateResolver(out var store,
            OfferTool(CapabilityGatedToolName),
            OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: [CapabilityGatedToolName, "GetCurrentTime"], modelProfile: ToolCapableModel);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, ToolCapableModel, retrievalQuery: null, supportsTools: false).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Equal(expected: 0, resolved!.AllowedTools.Count);
    }

    [Test]
    public async Task ResolveAsync_WhenModelHasToolsCapability_KeepsOfferedTools()
    {
        // Contrast: the same setup with supportsTools=true keeps the name-gated projection (today's behaviour).
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: ["GetCurrentTime"], modelProfile: ToolCapableModel);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, ToolCapableModel).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Contains(resolved!.AllowedTools, tool => tool.Name == "GetCurrentTime");
    }

    [Test]
    public async Task ResolveAsync_WhenDefinitionPinsNoModel_GatesByCallerActiveModelAndModelProfileIsNull()
    {
        // A definition with a NULL ModelProfile must fall back to the caller's active model for capability gating, and
        // the projection's ModelProfile must stay null (no pinned model to carry forward).
        string? observedModelId = null;
        var resolver = CreateResolver(out var store,
            modelId => observedModelId = modelId,
            OfferTool(CapabilityGatedToolName),
            OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: [CapabilityGatedToolName, "GetCurrentTime"], modelProfile: null);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, ToolCapableModel).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.True(resolved!.ModelProfile is null, "A definition pinning no model must project a null ModelProfile.");
        AssertEx.Equal(ToolCapableModel, observedModelId);
        // The caller's model is tool-capable, so the gated tool survives — proving the caller id (not null) drove gating.
        AssertEx.Contains(resolved.AllowedTools, tool => tool.Name == CapabilityGatedToolName);
    }

    [Test]
    public async Task ResolveAsync_WhenHonorModelProfileFalse_SuppressesPinForGatingAndProjectsNullModelProfile()
    {
        // The user explicitly picked a concrete model in the chat dropdown, so the caller passes honorModelProfile=false.
        // The definition's pinned ModelProfile is suppressed entirely: the projection's ModelProfile is null (so the
        // caller's `resolved?.ModelProfile ?? activeModel` yields the user's pick) AND the tool offer is gated by the
        // caller's active model, NOT the pin. Here the pin is a NON-tool-capable model while the user picked a
        // tool-capable one, so the capability-gated tool surviving proves the user's model — not the pin — drove gating.
        string? observedModelId = null;
        var resolver = CreateResolver(out var store,
            modelId => observedModelId = modelId,
            OfferTool(CapabilityGatedToolName),
            OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: [CapabilityGatedToolName, "GetCurrentTime"], modelProfile: IncapableModel);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, ToolCapableModel, retrievalQuery: null, supportsTools: true, honorModelProfile: false).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.True(resolved!.ModelProfile is null, "A suppressed pin must project a null ModelProfile so the user's pick wins.");
        AssertEx.Equal(ToolCapableModel, observedModelId);
        AssertEx.Contains(resolved.AllowedTools, tool => tool.Name == CapabilityGatedToolName);
    }

    [Test]
    public async Task ResolveAsync_WhenHonorModelProfileTrue_KeepsPinForGatingAndProjectsPin()
    {
        // Contrast (default precedence, no explicit user pick): honorModelProfile=true keeps the pin. The projection
        // carries the pinned model AND the offer is gated by it — the pin being a NON-tool-capable model withholds the
        // capability-gated tool even though the caller's active model is tool-capable.
        string? observedModelId = null;
        var resolver = CreateResolver(out var store,
            modelId => observedModelId = modelId,
            OfferTool(CapabilityGatedToolName),
            OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: [CapabilityGatedToolName, "GetCurrentTime"], modelProfile: IncapableModel);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, ToolCapableModel, retrievalQuery: null, supportsTools: true, honorModelProfile: true).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Equal(IncapableModel, resolved!.ModelProfile);
        AssertEx.Equal(IncapableModel, observedModelId);
        AssertEx.False(resolved.AllowedTools.Any(tool => tool.Name == CapabilityGatedToolName),
            "An honored non-tool-capable pin must gate out the capability-gated tool.");
    }

    [Test]
    public async Task ResolveAsync_BoundProjection_ProducesSameConfigHashAsHandBuiltEquivalent()
    {
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: ["GetCurrentTime"], version: 7, modelProfile: "qwen3:8b", reasoningEffort: "low");
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b").ConfigureAwait(false);
        AssertEx.NotNull(resolved);

        var builder = new LocalChatRuntimePackageBuilder();
        var projectedPackage = builder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            Guid.NewGuid(),
            resolved!.ResolvedSystemPrompt,
            [],
            resolved.ModelProfile,
            resolved.AgentDefinitionVersion,
            AllowedTools: resolved.AllowedTools,
            ReasoningEffort: resolved.ReasoningEffort));

        var handBuiltPackage = builder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            Guid.NewGuid(),
            SystemPrompt,
            [],
            "qwen3:8b",
            AgentDefinitionVersion: 7,
            AllowedTools: [OfferTool("GetCurrentTime")],
            ReasoningEffort: "low"));

        AssertEx.Equal(handBuiltPackage.ConfigHash, projectedPackage.ConfigHash);
    }

    [Test]
    public async Task ResolveAsync_NameOrDescriptionOnlyChange_DoesNotChangeConfigHash()
    {
        var builder = new LocalChatRuntimePackageBuilder();
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"));

        // Same config-affecting fields (instructions/tools/model/reasoning/version), different Name/Description. The
        // store owns the version; a name/description-only edit does not bump it, so the hash is unchanged.
        var first = CreateDefinition("Alpha", "first", ["GetCurrentTime"], version: 2);
        var second = first with
        {
            Name = "Beta",
            Description = "second"
        };

        var hashFirst = await ResolveAndHashAsync(resolver, store, builder, first).ConfigureAwait(false);
        var hashSecond = await ResolveAndHashAsync(resolver, store, builder, second).ConfigureAwait(false);

        AssertEx.Equal(hashFirst, hashSecond);
    }

    [Test]
    public async Task ResolveAsync_VersionInstructionsOrToolChange_ChangesConfigHash()
    {
        var builder = new LocalChatRuntimePackageBuilder();
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"), OfferTool("Calculate"));

        var baseDefinition = CreateDefinition(allowedTools: ["GetCurrentTime"], version: 1);
        var versionBumped = baseDefinition with
        {
            Version = 2
        };
        var instructionsChanged = baseDefinition with
        {
            Instructions = "A different system prompt."
        };
        var toolsChanged = baseDefinition with
        {
            AllowedToolNames = ["GetCurrentTime", "Calculate"]
        };

        var baseHash = await ResolveAndHashAsync(resolver, store, builder, baseDefinition).ConfigureAwait(false);
        var versionHash = await ResolveAndHashAsync(resolver, store, builder, versionBumped).ConfigureAwait(false);
        var instructionsHash = await ResolveAndHashAsync(resolver, store, builder, instructionsChanged).ConfigureAwait(false);
        var toolsHash = await ResolveAndHashAsync(resolver, store, builder, toolsChanged).ConfigureAwait(false);

        AssertEx.True(baseHash != versionHash, "Bumping Version must change the config hash.");
        AssertEx.True(baseHash != instructionsHash, "Changing Instructions must change the config hash.");
        AssertEx.True(baseHash != toolsHash, "Changing the tool set must change the config hash.");
    }

    [Test]
    public async Task ResolveAsync_WhenPlaybookDisabled_KeepsInstructionsByteIdenticalAndSkipsQuery()
    {
        var resolver = CreateResolverWithPlaybook(out var store, out var playbookStore, OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: ["GetCurrentTime"], playbookEnabled: false);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Equal(SystemPrompt, resolved!.ResolvedSystemPrompt);
        // The default-path regression guard: a disabled playbook must not even query the store.
        await playbookStore.DidNotReceive().ListEnabledByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ResolveAsync_WhenPlaybookEnabledButNoActions_KeepsInstructionsByteIdentical()
    {
        // PlaybookEnabled=true but the store returns no enabled actions: the composer is a no-op, so the prompt is still
        // byte-identical to Instructions and the config hash is unchanged.
        var resolver = CreateResolverWithPlaybook(out var store, out var playbookStore, OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: ["GetCurrentTime"], playbookEnabled: true);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);
        playbookStore.ListEnabledByAgentAsync(definition.Id, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Equal(SystemPrompt, resolved!.ResolvedSystemPrompt);
    }

    [Test]
    public async Task ResolveAsync_WhenPlaybookEnabledWithActions_AppendsBehaviorsInStoreOrder()
    {
        var resolver = CreateResolverWithPlaybook(out var store, out var playbookStore, OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: ["GetCurrentTime"], playbookEnabled: true);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);
        // The store fast-path returns enabled actions already ordered by Priority; the resolver must not re-sort, so the
        // composed prompt preserves this exact order.
        playbookStore.ListEnabledByAgentAsync(definition.Id, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([
                         EnabledAction(definition.Id, "Run the tests first.", priority: 1),
                         EnabledAction(definition.Id, "Prefer small commits.", priority: 5)
                     ]));

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        var expected = SystemPrompt + "\n\n## Operating Playbook\n- Run the tests first.\n- Prefer small commits.";
        AssertEx.Equal(expected, resolved!.ResolvedSystemPrompt);
    }

    [Test]
    public async Task Retrieval_WhenConversationMemoryExcluded_StillInjects()
    {
        // write-only suppression invariant: the memory-excluded (temporary-chat) flag suppresses EXTRACTION only,
        // never retrieval. The resolver — the injection path — has NO conversation/temp parameter at all (its signature
        // is agentId/model/query/supportsTools), so it structurally cannot be gated on conversation state: a temp chat
        // still gets the agent's existing Enabled memory composed into the resolved prompt exactly like a normal chat.
        var resolver = CreateResolverWithPlaybook(out var store, out var playbookStore, OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: ["GetCurrentTime"], playbookEnabled: true);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);
        playbookStore.ListEnabledByAgentAsync(definition.Id, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([
                         EnabledAction(definition.Id, "Always cite the source.", priority: 1)
                     ]));

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Contains(resolved!.ResolvedSystemPrompt, "Always cite the source.");
    }

    [Test]
    public async Task ResolveAsync_EnablingPlaybook_ChangesConfigHashVersusDisabled()
    {
        var builder = new LocalChatRuntimePackageBuilder();
        var resolver = CreateResolverWithPlaybook(out var store, out var playbookStore, OfferTool("GetCurrentTime"));

        var disabled = CreateDefinition(allowedTools: ["GetCurrentTime"], version: 1, playbookEnabled: false);
        var enabled = disabled with
        {
            PlaybookEnabled = true
        };
        playbookStore.ListEnabledByAgentAsync(enabled.Id, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([
                         EnabledAction(enabled.Id, "Run the tests first.", priority: 1)
                     ]));

        var disabledHash = await ResolveAndHashAsync(resolver, store, builder, disabled).ConfigureAwait(false);
        var enabledHash = await ResolveAndHashAsync(resolver, store, builder, enabled).ConfigureAwait(false);

        AssertEx.True(disabledHash != enabledHash, "Enabling a playbook with an action must change the config hash.");
    }

    [Test]
    public async Task ConfigHash_WhenInjectedMemoryChanges_ChangesDigest()
    {
        // Resume-safety: injected memory rides ResolvedSystemPrompt, which is a config-hash input. Changing WHICH memory
        // is injected (different behaviour text) must move the digest — that is the intended, correct behaviour and proves
        // memory is genuinely in the hashed prompt (not bypassing it via a per-invocation provider).
        var builder = new LocalChatRuntimePackageBuilder();
        var resolver = CreateResolverWithPlaybook(out var store, out var playbookStore, OfferTool("GetCurrentTime"));

        var definition = CreateDefinition(allowedTools: ["GetCurrentTime"], version: 1, playbookEnabled: true);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        playbookStore.ListEnabledByAgentAsync(definition.Id, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([EnabledAction(definition.Id, "Run the tests first.", priority: 1)]));
        var firstHash = await ResolveAndHashAsync(resolver, store, builder, definition).ConfigureAwait(false);

        playbookStore.ListEnabledByAgentAsync(definition.Id, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([EnabledAction(definition.Id, "Prefer small commits.", priority: 1)]));
        var secondHash = await ResolveAndHashAsync(resolver, store, builder, definition).ConfigureAwait(false);

        AssertEx.True(firstHash != secondHash, "Changing the injected memory text must change the config hash (memory rides the hashed prompt).");
    }

    [Test]
    public async Task ResolvedPrompt_NoMemory_ByteIdenticalToPreFeaturePath()
    {
        // A no-memory resolve (playbook disabled, AND playbook enabled but no actions) must produce a ResolvedSystemPrompt
        // and config hash byte-identical to the disabled/pre-feature path — scope routing and the token budget must have
        // ZERO effect when there is no memory to inject.
        var builder = new LocalChatRuntimePackageBuilder();
        var resolver = CreateResolverWithPlaybook(out var store, out var playbookStore, OfferTool("GetCurrentTime"));

        var disabled = CreateDefinition(allowedTools: ["GetCurrentTime"], version: 1, playbookEnabled: false);
        var enabledNoActions = disabled with
        {
            PlaybookEnabled = true
        };
        playbookStore.ListEnabledByAgentAsync(enabledNoActions.Id, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));

        store.GetByIdAsync(disabled.Id, Arg.Any<CancellationToken>()).Returns(disabled);
        var disabledResolved = await resolver.ResolveAsync(disabled.Id, "qwen3:8b").ConfigureAwait(false);
        var disabledHash = await ResolveAndHashAsync(resolver, store, builder, disabled).ConfigureAwait(false);

        var enabledResolved = await resolver.ResolveAsync(enabledNoActions.Id, "qwen3:8b").ConfigureAwait(false);
        var enabledHash = await ResolveAndHashAsync(resolver, store, builder, enabledNoActions).ConfigureAwait(false);

        AssertEx.NotNull(disabledResolved);
        AssertEx.NotNull(enabledResolved);
        AssertEx.Equal(SystemPrompt, disabledResolved!.ResolvedSystemPrompt);
        AssertEx.Equal(SystemPrompt, enabledResolved!.ResolvedSystemPrompt);
        AssertEx.Equal(disabledHash, enabledHash, "A no-memory resolve must hash identically to the pre-feature path.");
    }

    [Test]
    public async Task ResolveAsync_WhenEnabledAtOrBelowThreshold_UsesStaticPrependAndDoesNotConsultRanker()
    {
        // Two enabled actions, threshold 8: below the threshold the ranker is never consulted and the prompt is the full
        // static prepend — byte-identical to Compose(base, enabled).
        var ranker = new RecordingRanker();
        var resolver = BuildResolverWithRanker(out var store, out var playbookStore, ranker, threshold: 8, topK: 8, OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: ["GetCurrentTime"], playbookEnabled: true);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);
        playbookStore.ListEnabledByAgentAsync(definition.Id, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([
                         EnabledAction(definition.Id, "Run the tests first.", priority: 1),
                         EnabledAction(definition.Id, "Prefer small commits.", priority: 5)
                     ]));

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b", "anything").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        var expected = SystemPrompt + "\n\n## Operating Playbook\n- Run the tests first.\n- Prefer small commits.";
        AssertEx.Equal(expected, resolved!.ResolvedSystemPrompt);
        AssertEx.Equal(expected: 0, ranker.CallCount);
    }

    [Test]
    public async Task ResolveAsync_WhenAboveThresholdWithBlankQuery_UsesStaticPrependAndDoesNotConsultRanker()
    {
        // Three enabled actions, threshold 2 (above it): but a blank query must NOT engage retrieval — the full static
        // prepend is kept and the ranker is never consulted.
        var ranker = new RecordingRanker();
        var resolver = BuildResolverWithRanker(out var store, out var playbookStore, ranker, threshold: 2, topK: 2, OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: ["GetCurrentTime"], playbookEnabled: true);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);
        playbookStore.ListEnabledByAgentAsync(definition.Id, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([
                         EnabledAction(definition.Id, "Run the tests first.", priority: 1),
                         EnabledAction(definition.Id, "Prefer small commits.", priority: 5),
                         EnabledAction(definition.Id, "Write a changelog.", priority: 9)
                     ]));

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b", "   ").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        var expected = SystemPrompt + "\n\n## Operating Playbook\n- Run the tests first.\n- Prefer small commits.\n- Write a changelog.";
        AssertEx.Equal(expected, resolved!.ResolvedSystemPrompt);
        AssertEx.Equal(expected: 0, ranker.CallCount);
    }

    [Test]
    public async Task ResolveAsync_WhenAboveThresholdWithQuery_InjectsTopKReorderedByPriorityThenCreatedAt()
    {
        // Three enabled actions, threshold 2, top-k 2, non-blank query: the ranker is consulted once. The fake returns the
        // two it chooses out-of-priority-order; the resolver must re-impose Priority-then-CreatedAtUtc before composing.
        var lowPriority = EnabledAction(Guid.Empty, "Prefer small commits.", priority: 5);
        var highPriority = EnabledAction(Guid.Empty, "Run the tests first.", priority: 1);
        var ignored = EnabledAction(Guid.Empty, "Write a changelog.", priority: 9);

        var ranker = new RecordingRanker([lowPriority, highPriority]);
        var resolver = BuildResolverWithRanker(out var store, out var playbookStore, ranker, threshold: 2, topK: 2, OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: ["GetCurrentTime"], playbookEnabled: true);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);
        playbookStore.ListEnabledByAgentAsync(definition.Id, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([highPriority, lowPriority, ignored]));

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b", "run the tests").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Equal(expected: 1, ranker.CallCount);
        // Re-ordered by Priority ascending: highPriority (1) before lowPriority (5); the ignored third action is absent.
        var expected = SystemPrompt + "\n\n## Operating Playbook\n- Run the tests first.\n- Prefer small commits.";
        AssertEx.Equal(expected, resolved!.ResolvedSystemPrompt);
    }

    [Test]
    public async Task ResolveAsync_WhenPlaybookEnabledButNoActions_AboveThresholdNeverEngagesRanker()
    {
        // The empty-set guard holds even when a query is present: no enabled actions => byte-identical base, ranker untouched.
        var ranker = new RecordingRanker();
        var resolver = BuildResolverWithRanker(out var store, out var playbookStore, ranker, threshold: 0, topK: 8, OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: ["GetCurrentTime"], playbookEnabled: true);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);
        playbookStore.ListEnabledByAgentAsync(definition.Id, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b", "anything").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Equal(SystemPrompt, resolved!.ResolvedSystemPrompt);
        AssertEx.Equal(expected: 0, ranker.CallCount);
    }

    private const string ScaffoldText = "You are a locally-run agent. Ground every claim; use tools when they help.";

    [Test]
    public async Task ResolveAsync_PrependsScaffoldAheadOfPersonaByDefault()
    {
        var resolver = CreateResolverWithScaffold(out var store, ScaffoldText, OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: ["GetCurrentTime"]);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Equal($"{ScaffoldText}\n\n{SystemPrompt}", resolved!.ResolvedSystemPrompt);
    }

    [Test]
    public async Task ResolveAsync_WhenDisableBaseScaffold_KeepsPersonaOnlyByteIdentical()
    {
        // Opt-out: the resolved prompt must be exactly the persona Instructions, with no scaffold prepended — the
        // config-hash-stability guarantee for a definition that opts out.
        var resolver = CreateResolverWithScaffold(out var store, ScaffoldText, OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: ["GetCurrentTime"], disableBaseScaffold: true);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Equal(SystemPrompt, resolved!.ResolvedSystemPrompt);
    }

    [Test]
    public async Task ResolveAsync_ScaffoldPrependsAheadOfPlaybookComposedPersona()
    {
        // Composition order: scaffold, blank line, then the FULL persona prompt (Instructions + folded-in playbook
        // memories) — the existing playbook injection order is preserved underneath the scaffold.
        var resolver = CreateResolverWithScaffoldAndPlaybook(out var store, out var playbookStore, ScaffoldText, OfferTool("GetCurrentTime"));
        var definition = CreateDefinition(allowedTools: ["GetCurrentTime"], playbookEnabled: true);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);
        playbookStore.ListEnabledByAgentAsync(definition.Id, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([
                         EnabledAction(definition.Id, "Run the tests first.", priority: 1)
                     ]));

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        var expected = $"{ScaffoldText}\n\n{SystemPrompt}\n\n## Operating Playbook\n- Run the tests first.";
        AssertEx.Equal(expected, resolved!.ResolvedSystemPrompt);
    }

    [Test]
    public async Task ResolveAsync_TogglingDisableBaseScaffold_ChangesConfigHash()
    {
        // The scaffold rides ResolvedSystemPrompt like any other prompt text, so toggling the opt-out flag alone must
        // move the runtime package config hash — even though DisableBaseScaffold never bumps the definition's own
        // Version (mirrors PlaybookEnabled's non-config-affecting-for-Version class).
        var builder = new LocalChatRuntimePackageBuilder();
        var resolver = CreateResolverWithScaffold(out var store, ScaffoldText, OfferTool("GetCurrentTime"));

        var withScaffold = CreateDefinition(allowedTools: ["GetCurrentTime"], version: 1);
        var optedOut = withScaffold with
        {
            DisableBaseScaffold = true
        };

        var withScaffoldHash = await ResolveAndHashAsync(resolver, store, builder, withScaffold).ConfigureAwait(false);
        var optedOutHash = await ResolveAndHashAsync(resolver, store, builder, optedOut).ConfigureAwait(false);

        AssertEx.True(withScaffoldHash != optedOutHash, "Toggling DisableBaseScaffold must change the config hash.");
    }

    // Builds a resolver whose instruction provider returns a REAL (non-blank) scaffold, for the dedicated composition
    // tests above. Every other factory in this file passes an unconfigured stub (empty/null scaffold), which
    // BaseInstructionComposer treats as a no-op — that is what keeps the tool/playbook/hash tests above byte-identical.
    private static AgentDefinitionResolver CreateResolverWithScaffold(out IAgentDefinitionStore store, string scaffoldText, params AllowedToolDto[] offeredTools)
    {
        var instructionProvider = new FakeAgentInstructionProvider
        {
            BaseScaffold = scaffoldText
        };
        return BuildResolver(out store, out _, onGetOffered: null, offeredTools, instructionProvider);
    }

    private static AgentDefinitionResolver CreateResolverWithScaffoldAndPlaybook(out IAgentDefinitionStore store,
        out IPlaybookActionStore playbookStore,
        string scaffoldText,
        params AllowedToolDto[] offeredTools)
    {
        var instructionProvider = new FakeAgentInstructionProvider
        {
            BaseScaffold = scaffoldText
        };
        return BuildResolver(out store, out playbookStore, onGetOffered: null, offeredTools, instructionProvider);
    }

    private static async Task<string> ResolveAndHashAsync(IAgentDefinitionResolver resolver,
        IAgentDefinitionStore store,
        LocalChatRuntimePackageBuilder builder,
        AgentDefinitionRecord definition)
    {
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);
        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b").ConfigureAwait(false);
        AssertEx.NotNull(resolved);

        var package = builder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            Guid.NewGuid(),
            resolved!.ResolvedSystemPrompt,
            [],
            resolved.ModelProfile,
            resolved.AgentDefinitionVersion,
            AllowedTools: resolved.AllowedTools,
            ReasoningEffort: resolved.ReasoningEffort));

        return package.ConfigHash;
    }

    // A capability-HONORING stub that mirrors the real LocalToolOfferProvider gating: run_in_agent_home is offered
    // only to a tool-capable model id (here, ToolCapableModel); any other or null id gets the catalog minus that tool.
    // This exercises the model-id gating path the resolver drives via the EFFECTIVE model (def.ModelProfile ?? caller).
    // The Action<string?> records the model id GetOfferedTools was actually called with, for the null-profile test.
    private static AgentDefinitionResolver CreateResolver(out IAgentDefinitionStore store,
        Action<string?>? onGetOffered = null,
        params AllowedToolDto[] offeredTools)
    {
        return BuildResolver(out store, out _, onGetOffered, offeredTools);
    }

    private static AgentDefinitionResolver CreateResolver(out IAgentDefinitionStore store, params AllowedToolDto[] offeredTools)
    {
        return BuildResolver(out store, out _, onGetOffered: null, offeredTools);
    }

    // Builds a resolver over a caller-supplied IToolApprovalPolicy so the seam-D end-to-end test can prove the node
    // policy is applied during projection (every other factory uses the Permissive no-op floor).
    private static AgentDefinitionResolver CreateResolverWithPolicy(out IAgentDefinitionStore store,
        IToolApprovalPolicy toolApprovalPolicy,
        params AllowedToolDto[] offeredTools)
    {
        return BuildResolver(out store, out _, onGetOffered: null, offeredTools, instructionProvider: null, toolApprovalPolicy);
    }

    // Exposes the playbook store so the playbook-injection tests can stub ListEnabledByAgentAsync / assert it is not
    // queried on the disabled path. A distinct name avoids overload collision with the params-only CreateResolver.
    private static AgentDefinitionResolver CreateResolverWithPlaybook(out IAgentDefinitionStore store,
        out IPlaybookActionStore playbookStore,
        params AllowedToolDto[] offeredTools)
    {
        return BuildResolver(out store, out playbookStore, onGetOffered: null, offeredTools);
    }

    // Exposes both the definition store and a real (stubbable) skill store so the skill-resolution tests can configure
    // the enabled-by-ids fast path and assert the resolver drops missing/disabled assignments.
    private static AgentDefinitionResolver CreateResolverWithSkills(out IAgentDefinitionStore store,
        out IAgentSkillStore skillStore)
    {
        store = Substitute.For<IAgentDefinitionStore>();
        var playbookStore = Substitute.For<IPlaybookActionStore>();
        playbookStore.ListEnabledByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        var offerProvider = Substitute.For<ILocalToolOfferProvider>();
        offerProvider.GetOfferedTools(Arg.Any<string?>()).Returns([]);
        offerProvider.GetOfferedToolsForProfile(Arg.Any<string?>()).Returns([]);
        offerProvider.GetKnownToolNames().Returns([]);
        skillStore = Substitute.For<IAgentSkillStore>();
        return new AgentDefinitionResolver(store,
            playbookStore,
            skillStore,
            offerProvider,
            new LexicalPlaybookRetrievalRanker(),
            Options.Create(new PlaybookRetrievalOptions()),
            new FakeAgentInstructionProvider(),
            Substitute.For<IModelCapabilityResolver>(),
            new PermissiveToolApprovalPolicy(),
            NullLogger<AgentDefinitionResolver>.Instance);
    }

    private static AgentSkillRecord SkillRecord(Guid id, string name, string description, string body, int version = 1)
    {
        return new AgentSkillRecord(id, name, description, body, Enabled: true, version, CreatedAtUtc: 10, UpdatedAtUtc: 10);
    }

    // Builds a resolver over the REAL LocalToolOfferProvider so the R1 tests observe the actual knowledge-tool
    // withholding (not a mock). CloudPinnedModel is in the tool-capable allow-list, so the knowledge tools WOULD be
    // offered to it but for the provider-locality gate the resolver now applies to the effective (pinned) model.
    private static AgentDefinitionResolver BuildRealOfferResolver(out IAgentDefinitionStore store,
        bool allowCloudKnowledgeAccess,
        IModelCapabilityResolver capabilityResolver)
    {
        store = Substitute.For<IAgentDefinitionStore>();
        var playbookStore = Substitute.For<IPlaybookActionStore>();
        playbookStore.ListEnabledByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        var offerProvider = new LocalToolOfferProvider(new LocalAgentToolRegistry(),
            new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
            [CloudPinnedModel],
            allowCloudKnowledgeAccess);
        return new AgentDefinitionResolver(store,
            playbookStore,
            CreateEmptySkillStore(),
            offerProvider,
            new LexicalPlaybookRetrievalRanker(),
            Options.Create(new PlaybookRetrievalOptions()),
            new FakeAgentInstructionProvider(),
            capabilityResolver,
            new PermissiveToolApprovalPolicy(),
            NullLogger<AgentDefinitionResolver>.Instance);
    }

    private static AgentDefinitionResolver BuildResolver(out IAgentDefinitionStore store,
        out IPlaybookActionStore playbookStore,
        Action<string?>? onGetOffered,
        AllowedToolDto[] offeredTools,
        IAgentInstructionProvider? instructionProvider = null,
        IToolApprovalPolicy? toolApprovalPolicy = null)
    {
        store = Substitute.For<IAgentDefinitionStore>();
        playbookStore = Substitute.For<IPlaybookActionStore>();
        // Default: no enabled playbook actions, so the composer is a no-op and the resolved prompt stays byte-identical.
        playbookStore.ListEnabledByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        var offerProvider = Substitute.For<ILocalToolOfferProvider>();
        offerProvider.GetOfferedTools(Arg.Any<string?>()).Returns(callInfo =>
        {
            var modelId = callInfo.ArgAt<string?>(0);
            onGetOffered?.Invoke(modelId);
            return GateOffer(offeredTools, modelId);
        });
        // The profile-intersection pool mirrors the whole offer PLUS spawn_subagent (still capability-gated), matching
        // the real LocalToolOfferProvider.GetOfferedToolsForProfile asymmetry that keeps spawn out of the mode-off path.
        // The model-id observation fires here too, since a non-default profile gates via THIS method.
        offerProvider.GetOfferedToolsForProfile(Arg.Any<string?>()).Returns(callInfo =>
        {
            var modelId = callInfo.ArgAt<string?>(0);
            onGetOffered?.Invoke(modelId);
            return GateProfilePool(offeredTools, modelId);
        });
        offerProvider.GetKnownToolNames().Returns([.. offeredTools.Select(static tool => tool.Name)]);
        return new AgentDefinitionResolver(store,
            playbookStore,
            CreateEmptySkillStore(),
            offerProvider,
            new LexicalPlaybookRetrievalRanker(),
            Options.Create(new PlaybookRetrievalOptions()),
            // Unconfigured (null) returns null/empty from GetBaseScaffold, which BaseInstructionComposer treats as
            // "no scaffold" — so every pre-existing tool/playbook/hash test above keeps asserting the bare persona
            // prompt unchanged. Scaffold composition itself is covered by the dedicated tests below.
            instructionProvider ?? new FakeAgentInstructionProvider(),
            Substitute.For<IModelCapabilityResolver>(),
            toolApprovalPolicy ?? new PermissiveToolApprovalPolicy(),
            NullLogger<AgentDefinitionResolver>.Instance);
    }

    private const string SpawnToolName = "spawn_subagent";

    // The whole offer for a model: high-risk capability-gated tools are withheld from an incapable model; spawn_subagent
    // is NEVER in this whole offer (mode-off / Default-Assistant path).
    private static AllowedToolDto[] GateOffer(AllowedToolDto[] offeredTools, string? modelId)
    {
        var capable = modelId is not null && string.Equals(modelId, ToolCapableModel, StringComparison.Ordinal);
        return capable
            ? [.. offeredTools.Where(static tool => !string.Equals(tool.Name, SpawnToolName, StringComparison.Ordinal))]
            :
            [
                .. offeredTools.Where(static tool => !string.Equals(tool.Name, CapabilityGatedToolName, StringComparison.Ordinal)
                                                     && !string.Equals(tool.Name, SpawnToolName, StringComparison.Ordinal))
            ];
    }

    // The profile-intersection pool: the whole offer plus spawn_subagent when the model is tool-capable.
    private static AllowedToolDto[] GateProfilePool(AllowedToolDto[] offeredTools, string? modelId)
    {
        var whole = GateOffer(offeredTools, modelId);
        var capable = modelId is not null && string.Equals(modelId, ToolCapableModel, StringComparison.Ordinal);
        var spawn = offeredTools.FirstOrDefault(static tool => string.Equals(tool.Name, SpawnToolName, StringComparison.Ordinal));
        return capable && spawn is not null ? [.. whole, spawn] : whole;
    }

    // The skill picklist is empty for these tool/playbook/retrieval tests, so a skill store that returns no enabled
    // skills keeps the resolved set empty (the resolver only calls ListEnabledByIdsAsync for a non-empty picklist).
    private static IAgentSkillStore CreateEmptySkillStore()
    {
        var skillStore = Substitute.For<IAgentSkillStore>();
        skillStore.ListEnabledByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<IReadOnlyList<AgentSkillRecord>>([]));
        return skillStore;
    }

    // Builds a resolver with a caller-supplied ranker + explicit retrieval threshold/top-k, so the retrieval-gate tests
    // can assert the ranker is consulted only above the threshold (with a non-blank query) and that the result is
    // re-ordered by Priority/CreatedAtUtc.
    private static AgentDefinitionResolver BuildResolverWithRanker(out IAgentDefinitionStore store,
        out IPlaybookActionStore playbookStore,
        IPlaybookRetrievalRanker ranker,
        int threshold,
        int topK,
        params AllowedToolDto[] offeredTools)
    {
        store = Substitute.For<IAgentDefinitionStore>();
        playbookStore = Substitute.For<IPlaybookActionStore>();
        playbookStore.ListEnabledByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        var offerProvider = Substitute.For<ILocalToolOfferProvider>();
        offerProvider.GetOfferedTools(Arg.Any<string?>()).Returns(callInfo =>
            GateOffer(offeredTools, callInfo.ArgAt<string?>(0)));
        offerProvider.GetOfferedToolsForProfile(Arg.Any<string?>()).Returns(callInfo =>
            GateProfilePool(offeredTools, callInfo.ArgAt<string?>(0)));
        offerProvider.GetKnownToolNames().Returns([.. offeredTools.Select(static tool => tool.Name)]);
        var retrievalOptions = Options.Create(new PlaybookRetrievalOptions
        {
            RetrievalThreshold = threshold,
            TopK = topK
        });
        return new AgentDefinitionResolver(store,
            playbookStore,
            CreateEmptySkillStore(),
            offerProvider,
            ranker,
            retrievalOptions,
            new FakeAgentInstructionProvider(),
            Substitute.For<IModelCapabilityResolver>(),
            new PermissiveToolApprovalPolicy(),
            NullLogger<AgentDefinitionResolver>.Instance);
    }

    private static AgentDefinitionRecord CreateDefinition(string name = "Agent",
        string? description = null,
        IReadOnlyList<string>? allowedTools = null,
        IReadOnlyDictionary<string, bool>? toolApprovals = null,
        int version = 1,
        string? modelProfile = "qwen3:8b",
        string? reasoningEffort = null,
        bool playbookEnabled = false,
        IReadOnlyList<Guid>? allowedSkillIds = null,
        bool disableBaseScaffold = false)
    {
        return new AgentDefinitionRecord(Guid.NewGuid(),
            name,
            description,
            SystemPrompt,
            modelProfile,
            reasoningEffort,
            AgentDefinitionKind.Single,
            allowedTools ?? [],
            toolApprovals ?? new Dictionary<string, bool>(),
            OrchestrationTopologyJson: null,
            version,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10,
            playbookEnabled,
            AllowedSkillIds: allowedSkillIds,
            DisableBaseScaffold: disableBaseScaffold);
    }

    private static PlaybookActionRecord EnabledAction(Guid agentDefinitionId, string behavior, int priority)
    {
        return new PlaybookActionRecord(Guid.NewGuid(),
            agentDefinitionId,
            PlaybookActionState.Enabled,
            PlaybookActionSource.Manual,
            TriggerCondition: null,
            behavior,
            Scope: null,
            priority,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }

    // Stand-in offered tools default to a concrete ReadLocal category (never Unknown) to mirror the production invariant
    // that every real offered tool declares a category; a test that needs another category / the Unknown fail-closed path
    // passes it explicitly.
    private static AllowedToolDto OfferTool(string name, bool requiresApproval = false, ToolCategory category = ToolCategory.ReadLocal)
    {
        return new AllowedToolDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = ToolLocation.ClientLocal,
            ParameterSchema = "{\"type\":\"object\"}",
            RequiresApproval = requiresApproval,
            Category = category
        };
    }

    // A fake ranker that records how many times it was consulted and returns a fixed (deliberately out-of-order)
    // selection, so a test can assert both the gate (consulted only above the threshold with a query) and the resolver's
    // re-order of the ranker's output.
    private sealed class RecordingRanker(IReadOnlyList<PlaybookActionRecord>? selection = null) : IPlaybookRetrievalRanker
    {
        private readonly IReadOnlyList<PlaybookActionRecord>? _selection = selection;

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<PlaybookActionRecord>> SelectTopKAsync(string query,
            IReadOnlyList<PlaybookActionRecord> candidates,
            int k,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_selection ?? candidates);
        }
    }
}
