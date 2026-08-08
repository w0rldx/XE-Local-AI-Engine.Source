namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     AUD4-16: the chat-turn resolver must not read + decrypt the bound agent definition twice per send. The definition
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

        var resolution = await sut.ResolveAsync(activeModel: null,
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

        _ = await sut.ResolveAsync(activeModel: null,
            requiresInstalledChatModel: false,
            effectiveAgentId: agentId,
            retrievalQuery: null,
            userPickedConcreteModel: false,
            CancellationToken.None);

        // A bound orchestrator (rare) still reloads exactly once for the compiler; a null record yields no orchestration.
        await store.Received(1).GetByIdAsync(agentId, Arg.Any<CancellationToken>());
    }

    private static ChatTurnResolver CreateSut(IAgentDefinitionResolver resolver, IAgentDefinitionStore store, out IOrchestrationResolver orchestrationResolver)
    {
        orchestrationResolver = Substitute.For<IOrchestrationResolver>();
        return new ChatTurnResolver(resolver,
            store,
            orchestrationResolver,
            Substitute.For<IModelClassificationService>(),
            Substitute.For<ILocalModelProviderResolver>(),
            Substitute.For<IGgufModelCapabilityResolver>(),
            Substitute.For<IActiveCloudChatClientFactory>(),
            NullLogger<ChatTurnResolver>.Instance);
    }
}
