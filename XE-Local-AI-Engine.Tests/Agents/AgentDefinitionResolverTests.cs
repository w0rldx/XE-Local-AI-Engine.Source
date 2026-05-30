namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;
using XE_Local_AI_Engine.Tests.Testing;

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

        var resolved = await resolver.ResolveAsync(null, "qwen3:8b").ConfigureAwait(false);

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
        AssertEx.Equal(4, resolved.AgentDefinitionVersion);
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
        AssertEx.Equal(1, resolved!.AllowedTools.Count);
        AssertEx.Equal("Calculate", resolved.AllowedTools[0].Name);
    }

    [Test]
    public async Task ResolveAsync_AppliesToolApprovalOverrides_FallingBackToDescriptorFlag()
    {
        // The offer ships both tools as non-approval. The definition overrides one to require approval and leaves the
        // other to its descriptor default.
        var resolver = CreateResolver(out var store,
            OfferTool("GetCurrentTime", requiresApproval: false),
            OfferTool("Calculate", requiresApproval: false));
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
        AssertEx.Equal(true, gated.RequiresApproval);
        AssertEx.Equal(false, ungated.RequiresApproval);
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
            7,
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
        var first = CreateDefinition(name: "Alpha", description: "first", allowedTools: ["GetCurrentTime"], version: 2);
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
        var versionBumped = baseDefinition with { Version = 2 };
        var instructionsChanged = baseDefinition with { Instructions = "A different system prompt." };
        var toolsChanged = baseDefinition with { AllowedToolNames = ["GetCurrentTime", "Calculate"] };

        var baseHash = await ResolveAndHashAsync(resolver, store, builder, baseDefinition).ConfigureAwait(false);
        var versionHash = await ResolveAndHashAsync(resolver, store, builder, versionBumped).ConfigureAwait(false);
        var instructionsHash = await ResolveAndHashAsync(resolver, store, builder, instructionsChanged).ConfigureAwait(false);
        var toolsHash = await ResolveAndHashAsync(resolver, store, builder, toolsChanged).ConfigureAwait(false);

        AssertEx.True(baseHash != versionHash, "Bumping Version must change the config hash.");
        AssertEx.True(baseHash != instructionsHash, "Changing Instructions must change the config hash.");
        AssertEx.True(baseHash != toolsHash, "Changing the tool set must change the config hash.");
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
        store = Substitute.For<IAgentDefinitionStore>();
        var offerProvider = Substitute.For<ILocalToolOfferProvider>();
        offerProvider.GetOfferedTools(Arg.Any<string?>()).Returns(callInfo =>
        {
            var modelId = callInfo.ArgAt<string?>(0);
            onGetOffered?.Invoke(modelId);
            var capable = modelId is not null && string.Equals(modelId, ToolCapableModel, StringComparison.Ordinal);
            return capable
                ? offeredTools
                : [.. offeredTools.Where(static tool => !string.Equals(tool.Name, CapabilityGatedToolName, StringComparison.Ordinal))];
        });
        offerProvider.GetKnownToolNames().Returns([.. offeredTools.Select(static tool => tool.Name)]);
        return new AgentDefinitionResolver(store, offerProvider, NullLogger<AgentDefinitionResolver>.Instance);
    }

    private static AgentDefinitionResolver CreateResolver(out IAgentDefinitionStore store, params AllowedToolDto[] offeredTools)
    {
        return CreateResolver(out store, onGetOffered: null, offeredTools);
    }

    private static AgentDefinitionRecord CreateDefinition(string name = "Agent",
        string? description = null,
        IReadOnlyList<string>? allowedTools = null,
        IReadOnlyDictionary<string, bool>? toolApprovals = null,
        int version = 1,
        string? modelProfile = "qwen3:8b",
        string? reasoningEffort = null)
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
            null,
            version,
            10,
            10);
    }

    private static AllowedToolDto OfferTool(string name, bool requiresApproval = false)
    {
        return new AllowedToolDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = ToolLocation.ClientLocal,
            ParameterSchema = "{\"type\":\"object\"}",
            RequiresApproval = requiresApproval
        };
    }
}
