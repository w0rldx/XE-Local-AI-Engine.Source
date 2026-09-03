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

        // With an installed chat model the CAPABILITY HEAD stays the ACTIVE model — the think gate, the tool offer and
        // the locality flag are all still read from it, and the pin's definition record is never fetched.
        AssertEx.False(resolution.SupportsTools);
        AssertEx.False(resolution.SupportsThinking);
        await capabilityResolver.Received(1).ResolveAsync(activeModel, Arg.Any<CancellationToken>());
        await store.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        // The pin IS resolved once more, and for one thing only: the reasoning-budget flag, which is a property of the
        // template of the model that actually runs (see the ResolveWithPinAsync tests below). This assertion used to
        // read DidNotReceive, on the then-true premise that nothing downstream needed the pin's capabilities; grading
        // the budget against the active model shipped a cap llama-server accepted and ignored, so the extra lookup is
        // the fix rather than a regression. The gate assertions above are what keep the head itself from drifting.
        await capabilityResolver.Received(1).ResolveAsync(pinnedModel, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolveAsync_WhenTheBoundAgentPinsAnUnenforceableModel_ReportsTheBudgetUnenforceable()
    {
        // The active model can cap its reasoning; the pinned model the turn will actually run on cannot. Reading the
        // ACTIVE model's flag here emitted a budget llama-server accepts and then ignores, so the reasoning free-ran
        // while every layer above believed the cap held.
        var resolution = await ResolveWithPinAsync(activeModelEnforceable: true, pinnedModelEnforceable: false);

        AssertEx.False(resolution.ReasoningBudgetEnforceable, "the pinned model's template cannot be capped, so the turn must report the budget unenforceable");
    }

    [Test]
    public async Task ResolveAsync_WhenTheBoundAgentPinsAnEnforceableModel_ReportsTheBudgetEnforceable()
    {
        // The converse, so the fix cannot be a constant: an unenforceable ACTIVE model must not suppress the budget for
        // a pinned model that can enforce it.
        var resolution = await ResolveWithPinAsync(activeModelEnforceable: false, pinnedModelEnforceable: true);

        AssertEx.True(resolution.ReasoningBudgetEnforceable, "the pinned model is what runs, and its template renders a reasoning end marker");
    }

    [Test]
    public async Task ResolveAsync_WhenTheBoundAgentPinsNoModel_KeepsTheActiveModelsBudgetFlag()
    {
        // No honored pin: the effective model IS the active model, so the turn keeps the active model's flag and makes
        // no second lookup. Passing `true` for the pinned model here would flip the answer if the pin were consulted.
        var resolution = await ResolveWithPinAsync(activeModelEnforceable: false, pinnedModelEnforceable: null);

        AssertEx.False(resolution.ReasoningBudgetEnforceable, "with no pin the active model's flag is the turn's flag");
    }

    // The ONE turn shape a dispatcher model swap is allowed on: the node's default model ran because nobody asked for
    // a specific one. Named for the permission, so this is the only case that may return true.
    [Test]
    public async Task Resolve_WhenNoPickAndNoPin_AllowsAutoModelSwap()
    {
        var resolution = await ResolveWithPinAsync(activeModelEnforceable: true, pinnedModelEnforceable: null);

        AssertEx.True(resolution.AllowAutoModelSwap, "no explicit pick and no honored pin means the node chose the model, so it may be swapped");
    }

    // Both pinned shapes clear the permission: an explicit user pick and an honored agent pin are each a request for
    // THAT model, and the dispatcher must not answer it with a different one.
    [Test]
    [Arguments(true, null)]
    [Arguments(false, true)]
    public async Task Resolve_WhenUserPickedOrAgentPinned_DisallowsAutoModelSwap(bool userPickedConcreteModel, bool? pinnedModelEnforceable)
    {
        var resolution = await ResolveWithPinAsync(activeModelEnforceable: true, pinnedModelEnforceable, userPickedConcreteModel);

        AssertEx.False(resolution.AllowAutoModelSwap, "a pinned model — picked by the user or honored from the agent definition — is never swapped");
    }

    private static async Task<ChatTurnResolution> ResolveWithPinAsync(bool activeModelEnforceable,
        bool? pinnedModelEnforceable,
        bool userPickedConcreteModel = false)
    {
        const string ActiveModel = "active-gguf";
        const string PinnedModel = "pinned-gguf";

        var agentId = Guid.NewGuid();
        var store = Substitute.For<IAgentDefinitionStore>();
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("persona",
                    [],
                    ModelProfile: pinnedModelEnforceable is null ? null : PinnedModel,
                    ReasoningEffort: "high",
                    AgentDefinitionVersion: 1,
                    agentId,
                    "Agent",
                    Kind: AgentDefinitionKind.Single));

        var capabilityResolver = Substitute.For<IModelCapabilityResolver>();
        capabilityResolver.ResolveAsync(ActiveModel, Arg.Any<CancellationToken>())
                          .Returns(Task.FromResult(new ModelCapabilitySnapshot(SupportsThinking: true, SupportsTools: true, IsCloud: false)
                          {
                              ReasoningBudgetEnforceable = activeModelEnforceable
                          }));
        capabilityResolver.ResolveAsync(PinnedModel, Arg.Any<CancellationToken>())
                          .Returns(Task.FromResult(new ModelCapabilitySnapshot(SupportsThinking: true, SupportsTools: true, IsCloud: false)
                          {
                              ReasoningBudgetEnforceable = pinnedModelEnforceable ?? true
                          }));

        var sut = CreateSut(resolver, store, out _, capabilityResolver);

        return await sut.ResolveAsync(ActiveModel,
            requiresInstalledChatModel: false,
            effectiveAgentId: agentId,
            retrievalQuery: null,
            userPickedConcreteModel,
            CancellationToken.None);
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
