namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The chat-turn resolver must not read + decrypt the bound agent definition twice per send. The definition
///     resolver already loads it once; the orchestration branch reuses that resolution's <c>Kind</c> and only reloads for
///     a bound orchestrator (rare), so the common non-orchestrator path issues a single store read.
/// </summary>
public sealed class ChatTurnResolverTests
{
    [Test]
    public async Task ResolveAsync_WhenResolvedKindIsNotOrchestrator_SkipsTheSecondDefinitionRead()
    {
        var agentId = Guid.NewGuid();
        var store = Substitute.For<IAgentDefinitionStore>();
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("persona", [], ModelProfile: null, ReasoningEffort: null, AgentDefinitionVersion: 1, agentId, "Agent", Kind: AgentDefinitionKind.Single));

        var sut = CreateSut(resolver, store, out var orchestrationResolver);

        // An installed GGUF default (the common UI path): the capability head is the active model, so no pin lookup runs
        // and the only store read that could happen here is the orchestration reload.
        var resolution = await sut.ResolveAsync(activeModel: "local-gguf",
            requiresInstalledChatModel: false,
            effectiveAgentId: agentId,
            retrievalQuery: null,
            userPickedConcreteModel: false,
            CancellationToken.None);

        AssertEx.Null(resolution.Orchestration);
        // The definition was loaded once by the resolver; a non-orchestrator turn must NOT read it a second time.
        await store.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await orchestrationResolver.DidNotReceive()
                                   .ResolveAsync(Arg.Any<AgentDefinitionRecord>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolveAsync_WhenResolvedKindIsOrchestrator_ReadsTheDefinitionOnceToCompileOrchestration()
    {
        var agentId = Guid.NewGuid();
        var store = Substitute.For<IAgentDefinitionStore>();
        store.GetByIdAsync(agentId, Arg.Any<CancellationToken>()).Returns((AgentDefinitionRecord?)null);
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("persona", [], ModelProfile: null, ReasoningEffort: null, AgentDefinitionVersion: 1, agentId, "Orchestrator",
                    Kind: AgentDefinitionKind.Orchestrator));

        var sut = CreateSut(resolver, store, out _);

        // An installed GGUF default, so the single read this turn makes is the orchestration reload.
        _ = await sut.ResolveAsync(activeModel: "local-gguf",
            requiresInstalledChatModel: false,
            effectiveAgentId: agentId,
            retrievalQuery: null,
            userPickedConcreteModel: false,
            CancellationToken.None);

        // A bound orchestrator (rare) still reloads exactly once for the compiler; a null record yields no orchestration.
        await store.Received(1).GetByIdAsync(agentId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolveAsync_WhenNoInstalledChatModelButAgentPinsAModel_ClearsTheRequiresInstalledFlag()
    {
        var agentId = Guid.NewGuid();
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("persona", [], ModelProfile: "llama3.1:8b", ReasoningEffort: null, AgentDefinitionVersion: 1, agentId, "Agent",
                    Kind: AgentDefinitionKind.Single));

        var sut = CreateSut(resolver, Substitute.For<IAgentDefinitionStore>(), out _);

        var resolution = await sut.ResolveAsync(activeModel: null,
            requiresInstalledChatModel: true,
            effectiveAgentId: agentId,
            retrievalQuery: null,
            userPickedConcreteModel: false,
            CancellationToken.None);

        // The node has no installed GGUF chat model, but the agent's pin gives the turn a model to run — the runner's
        // "no chat model installed" guard must stand down instead of failing a perfectly runnable turn.
        AssertEx.Equal("llama3.1:8b", resolution.EffectiveModel);
        AssertEx.False(resolution.RequiresInstalledChatModel);
    }

    [Test]
    public async Task ResolveAsync_WhenNoInstalledChatModelAndNoAgentPin_KeepsTheRequiresInstalledFlag()
    {
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns((ResolvedAgentRuntime?)null);

        var sut = CreateSut(resolver, Substitute.For<IAgentDefinitionStore>(), out _);

        var resolution = await sut.ResolveAsync(activeModel: null,
            requiresInstalledChatModel: true,
            effectiveAgentId: null,
            retrievalQuery: null,
            userPickedConcreteModel: false,
            CancellationToken.None);

        // Nothing produced a model: the guard must survive so the UI still terminalizes as "model not installed".
        AssertEx.Null(resolution.EffectiveModel);
        AssertEx.True(resolution.RequiresInstalledChatModel);
    }

    [Test]
    public async Task ResolveAsync_WhenNoInstalledChatModelButAgentPinsAModel_ResolvesCapabilitiesFromThePin()
    {
        const string pinnedModel = "llama3.1:8b";
        var agentId = Guid.NewGuid();
        var store = Substitute.For<IAgentDefinitionStore>();
        store.GetByIdAsync(agentId, Arg.Any<CancellationToken>()).Returns(CreatePinningDefinition(agentId, pinnedModel));

        var capabilityResolver = Substitute.For<IModelCapabilityResolver>();
        capabilityResolver.ResolveAsync(null, Arg.Any<CancellationToken>()).Returns(default(ModelCapabilitySnapshot));
        capabilityResolver.ResolveAsync(pinnedModel, Arg.Any<CancellationToken>())
                          .Returns(new ModelCapabilitySnapshot(SupportsThinking: true, SupportsTools: true, IsCloud: false));

        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("persona", [], ModelProfile: pinnedModel, ReasoningEffort: null, AgentDefinitionVersion: 1, agentId, "Agent",
                    Kind: AgentDefinitionKind.Single));

        var sut = CreateSut(resolver, store, out _, capabilityResolver);

        var resolution = await sut.ResolveAsync(activeModel: null,
            requiresInstalledChatModel: true,
            effectiveAgentId: agentId,
            retrievalQuery: null,
            userPickedConcreteModel: false,
            CancellationToken.None);

        // The null active model advertises nothing, but the pin is what actually runs this turn — gating on the null head
        // would hand the agent resolver supportsTools:false and leave a work session unable to call its own tools.
        AssertEx.True(resolution.SupportsTools);
        AssertEx.True(resolution.SupportsThinking);
        await resolver.Received(1)
                      .ResolveAsync(agentId, null, null, supportsTools: true, honorModelProfile: true, activeModelIsCloud: false, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolveAsync_WhenActiveModelIsInstalledAndAgentPinsAModel_ResolvesCapabilitiesFromTheActiveModel()
    {
        const string activeModel = "local-gguf";
        const string pinnedModel = "llama3.1:8b";
        var agentId = Guid.NewGuid();
        var store = Substitute.For<IAgentDefinitionStore>();
        store.GetByIdAsync(agentId, Arg.Any<CancellationToken>()).Returns(CreatePinningDefinition(agentId, pinnedModel));

        var capabilityResolver = Substitute.For<IModelCapabilityResolver>();
        capabilityResolver.ResolveAsync(activeModel, Arg.Any<CancellationToken>()).Returns(default(ModelCapabilitySnapshot));
        capabilityResolver.ResolveAsync(pinnedModel, Arg.Any<CancellationToken>())
                          .Returns(new ModelCapabilitySnapshot(SupportsThinking: true, SupportsTools: true, IsCloud: false));

        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("persona", [], ModelProfile: pinnedModel, ReasoningEffort: null, AgentDefinitionVersion: 1, agentId, "Agent",
                    Kind: AgentDefinitionKind.Single));

        var sut = CreateSut(resolver, store, out _, capabilityResolver);

        var resolution = await sut.ResolveAsync(activeModel,
            requiresInstalledChatModel: true,
            effectiveAgentId: agentId,
            retrievalQuery: null,
            userPickedConcreteModel: false,
            CancellationToken.None);

        // With an installed chat model the head stays the ACTIVE model: the pin lookup never runs, so this path is
        // byte-identical to before the no-GGUF branch existed.
        AssertEx.False(resolution.SupportsTools);
        AssertEx.False(resolution.SupportsThinking);
        await capabilityResolver.Received(1).ResolveAsync(activeModel, Arg.Any<CancellationToken>());
        await capabilityResolver.DidNotReceive().ResolveAsync(pinnedModel, Arg.Any<CancellationToken>());
        await store.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private static AgentDefinitionRecord CreatePinningDefinition(Guid id, string pinnedModel)
    {
        return new AgentDefinitionRecord(id,
            "Agent",
            Description: null,
            "Persona.",
            pinnedModel,
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(),
            OrchestrationTopologyJson: null,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }

    private static ChatTurnResolver CreateSut(IAgentDefinitionResolver resolver,
        IAgentDefinitionStore store,
        out IOrchestrationResolver orchestrationResolver,
        IModelCapabilityResolver? capabilityResolver = null)
    {
        orchestrationResolver = Substitute.For<IOrchestrationResolver>();
        return new ChatTurnResolver(resolver,
            store,
            orchestrationResolver,
            capabilityResolver ?? Substitute.For<IModelCapabilityResolver>(),
            NullLogger<ChatTurnResolver>.Instance);
    }
}
