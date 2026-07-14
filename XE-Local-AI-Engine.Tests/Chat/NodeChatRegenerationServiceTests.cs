namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

public sealed class NodeChatRegenerationServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task RegenerateAsync_ProducesCompletedSiblingVariantInSameGroupAndStreamsEvents()
    {
        await using var provider = await BuildProviderAsync("regeneration.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        // Seed a completed turn: user question + a completed assistant answer (the "original" to regenerate).
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "what is 2+2?", CreatedAtUtc: 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, CreatedAtUtc: 12,
                             "model-x"))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(
                             new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 13, "four", Model: "model-x"))
                         .ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var runner = new RegenCompletingRunner(dispatcher);
        var service = new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelClassificationService(), CreateLocalModelProviderResolver(),
                CreateGgufModelCapabilityResolver(), Substitute.For<ICloudCredentialStore>(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            TimeProvider.System,
            NullLogger<NodeChatRegenerationService>.Instance);

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.RegenerateAsync(conversation.ConversationId, originalId).ConfigureAwait(false))
        {
            events.Add(streamEvent);
        }

        // Streams the same lifecycle as a normal send.
        AssertEx.True(events.Any(e => e.Type == ChatStreamEventTypes.AssistantQueued), "Expected assistant-queued.");
        AssertEx.True(events.Any(e => e.Type == ChatStreamEventTypes.AssistantStreaming), "Expected assistant-streaming.");
        AssertEx.True(events.Any(e => e.Type == ChatStreamEventTypes.AssistantDelta), "Expected assistant-delta.");
        var completed = events.Single(e => e.Type == ChatStreamEventTypes.AssistantCompleted);
        AssertEx.Equal("regenerated answer", completed.Content);

        // The regenerated variant is a COMPLETED sibling sharing one variant_group with the original.
        var variants = await persistence.ListMessageVariantsAsync(conversation.ConversationId, originalId).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, variants.Count);
        var groups = variants.Select(v => v.VariantGroupId).Distinct().ToList();
        AssertEx.Equal(expected: 1, groups.Count);
        AssertEx.True(groups[0] is not null, "The shared variant group id must be set.");

        var regenerated = variants.Single(v => v.MessageId == completed.MessageId);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, regenerated.Status);
        AssertEx.Equal("regenerated answer", regenerated.Content);
        AssertEx.Equal(originalId, regenerated.ParentMessageId);

        // The original is untouched (not overwritten) — regeneration is sibling, never in-place.
        var original = variants.Single(v => v.MessageId == originalId);
        AssertEx.Equal("four", original.Content);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, original.Status);
    }

    [Test]
    public async Task RegenerateAsync_WhenLocalDefaultOriginalAndNoChatModelInstalled_TerminalizesAsModelNotInstalled()
    {
        // Regenerating a "Local runtime default" turn (the original carried no explicit model) where the resolver finds
        // NO installed GGUF chat model must fail with FailureCategory.ModelNotInstalled — mirroring the send path — not
        // the generic Unexpected/ProviderUnreachable. The regenerate path is guarded too.
        await using var provider = await BuildProviderAsync("regeneration-no-chat-model.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        // Seed a completed local-default original turn: the placeholder + terminal carry NO model (request.Model null).
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "what is 2+2?", CreatedAtUtc: 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, CreatedAtUtc: 12))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 13, "four"))
                         .ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var runner = new RegenCompletingRunner(dispatcher);
        var service = new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelClassificationService(), CreateLocalModelProviderResolver(),
                CreateGgufModelCapabilityResolver(), Substitute.For<ICloudCredentialStore>(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            // Resolver reports no installed GGUF chat model (null).
            CreateLocalDefaultChatModelResolver(resolved: null, echoPersistedDefault: false),
            CreateMemoryExtractionDispatcher(),
            TimeProvider.System,
            NullLogger<NodeChatRegenerationService>.Instance);

        var drained = 0;
        await foreach (var _ in service.RegenerateAsync(conversation.ConversationId, originalId).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the regenerate to stream events.");
        AssertEx.NotNull(dispatcher.CurrentInvocation);
        AssertEx.Equal(FailureCategory.ModelNotInstalled, dispatcher.CurrentInvocation!.FailureCategory);
        AssertEx.Equal(InvocationStatus.Failed, dispatcher.CurrentInvocation.Status);
    }

    [Test]
    public async Task RegenerateAsync_WhenConversationBound_HydratesDefinitionPromptToolsAndVersion()
    {
        // End-to-end read-side proof: a conversation created WITH an agent_definition_id is read back through the real
        // persistence (GetConversationAsync selects the column), the resolver is consulted with that id, and the bound
        // projection reaches the regenerate runtime package. This guards the second hydration site against divergence.
        await using var provider = await BuildProviderAsync("regeneration-bound.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
        var agentDefinitionId = Guid.NewGuid();

        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Bound regen", "node", CreatedAtUtc: 10, AgentDefinitionId: agentDefinitionId))
                                            .ConfigureAwait(false);
        AssertEx.Equal(agentDefinitionId, conversation.AgentDefinitionId!.Value);

        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "what is 2+2?", CreatedAtUtc: 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, CreatedAtUtc: 12,
                             "model-x"))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(
                             new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 13, "four", Model: "model-x"))
                         .ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var runner = new RegenContextCapturingRunner(dispatcher);
        var boundTool = CreateLocalToolDto("Calculate", "{\"type\":\"object\"}");
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(agentDefinitionId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("Bound persona prompt.", [boundTool], "qwen3:8b", "high", AgentDefinitionVersion: 9));

        var service = new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelClassificationService(), CreateLocalModelProviderResolver(),
                CreateGgufModelCapabilityResolver(), Substitute.For<ICloudCredentialStore>(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = true
            }),
            StubNodeRuntimeSettings.Create().WithEnableTools(true).Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}")),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            TimeProvider.System,
            NullLogger<NodeChatRegenerationService>.Instance);

        var drained = 0;
        await foreach (var _ in service.RegenerateAsync(conversation.ConversationId, originalId, useLocalTools: true).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the regenerate to stream events.");
        // ResolvePrecedingUserTurnContent anchors the relevance-retrieval query to the user turn the
        // regenerate re-answers — here the seeded "what is 2+2?" — not just any string. This is the only direct
        // coverage of that variant-group-anchored query selection.
        await resolver.Received().ResolveAsync(agentDefinitionId, Arg.Any<string?>(), Arg.Is<string?>(query => query == "what is 2+2?"), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                      .ConfigureAwait(false);
        AssertEx.Equal("Bound persona prompt.", runner.LastSystemPrompt);
        AssertEx.Equal(expected: 9, runner.LastAgentDefinitionVersion);
        AssertEx.Equal("high", runner.LastReasoningEffort);
        AssertEx.Equal(expected: 1, runner.LastAllowedTools.Count);
        AssertEx.Equal("Calculate", runner.LastAllowedTools[0].Name);
        AssertEx.True(runner.LastOrchestrationSpec is null, "A single-agent regenerate must carry no orchestration spec.");
    }

    [Test]
    public async Task Regenerate_WhenOriginalCarriesConcreteModel_SuppressesAgentPin_StampsOriginalModel()
    {
        // Parity with the send path: the original turn recorded a CONCRETE model ("model-x") — an explicit user pick —
        // while the bound agent pins its own ModelProfile ("gemma3:4b"). The explicit pick must win over the pin for
        // BOTH the rerun (package model) AND the variant's persisted attribution, and the resolver is told
        // honorModelProfile=false. The resolver mock mirrors the real resolver: a suppressed pin projects a null
        // ModelProfile so `resolved?.ModelProfile ?? activeModel` yields the original model.
        const string originalModel = "model-x";
        await using var provider = await BuildProviderAsync("regeneration-effective-model.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
        var agentDefinitionId = Guid.NewGuid();

        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Effective-model regen", "node", CreatedAtUtc: 10, AgentDefinitionId: agentDefinitionId))
                                            .ConfigureAwait(false);
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "what is 2+2?", CreatedAtUtc: 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, CreatedAtUtc: 12,
                             originalModel))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 13, "four",
                             Model: originalModel))
                         .ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var runner = new RegenContextCapturingRunner(dispatcher);
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(agentDefinitionId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var honorModelProfile = callInfo.ArgAt<bool>(4);
                    return new ResolvedAgentRuntime("Pinned persona.", [], honorModelProfile ? "gemma3:4b" : null, ReasoningEffort: null, AgentDefinitionVersion: 9, agentDefinitionId, "Pinned Agent");
                });

        var service = new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelClassificationService(), CreateLocalModelProviderResolver(),
                CreateGgufModelCapabilityResolver(), Substitute.For<ICloudCredentialStore>(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            TimeProvider.System,
            NullLogger<NodeChatRegenerationService>.Instance);

        var newVariantId = Guid.Empty;
        await foreach (var streamEvent in service.RegenerateAsync(conversation.ConversationId, originalId).ConfigureAwait(false))
        {
            if (streamEvent.Type == ChatStreamEventTypes.AssistantPending)
            {
                newVariantId = streamEvent.MessageId;
            }
        }

        // The rerun executed on the original's explicit model, not the agent's pin.
        AssertEx.Equal(originalModel, runner.LastModelProfile);
        // The resolver was told to suppress the pin.
        await resolver.Received().ResolveAsync(agentDefinitionId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Is<bool>(honor => !honor), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                      .ConfigureAwait(false);
        // The persisted variant's attribution reflects the model that actually reran.
        var loaded = AssertEx.NotNull(await persistence.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var variant = loaded.Messages.Single(message => message.MessageId == newVariantId);
        AssertEx.Equal(originalModel, variant.Model);
    }

    [Test]
    public async Task Regenerate_ReusesOriginalTurnAgent()
    {
        // The regenerate reuses the ORIGINAL turn's recorded agent, NOT the conversation binding. The conversation is
        // bound to one agent; the original assistant turn was produced by a DIFFERENT agent (stamped on its metadata).
        // The resolver must be consulted with the original's agent id, and the variant must be stamped with the
        // re-resolved agent's name.
        await using var provider = await BuildProviderAsync("regeneration-reuses-original-agent.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
        var conversationAgentId = Guid.NewGuid();
        var originalTurnAgentId = Guid.NewGuid();

        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Reuse original agent", "node", CreatedAtUtc: 10, AgentDefinitionId: conversationAgentId))
                                            .ConfigureAwait(false);
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "what is 2+2?", CreatedAtUtc: 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        // The original assistant turn carries its own agent attribution (stamped at its send time).
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId,
                             originalId,
                             originalCorrelation.RequestId,
                             CreatedAtUtc: 12,
                             "model-x",
                             AgentDefinitionId: originalTurnAgentId,
                             AgentName: "Original Agent"))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(
                             new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 13, "four", Model: "model-x"))
                         .ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var runner = new RegenContextCapturingRunner(dispatcher);
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        // The agent was renamed since the original turn — the re-resolve picks up the fresh name.
        resolver.ResolveAsync(originalTurnAgentId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("Original persona.", [], "model-x", ReasoningEffort: null, AgentDefinitionVersion: 5, originalTurnAgentId, "Renamed Original Agent"));

        var service = new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelClassificationService(), CreateLocalModelProviderResolver(),
                CreateGgufModelCapabilityResolver(), Substitute.For<ICloudCredentialStore>(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            TimeProvider.System,
            NullLogger<NodeChatRegenerationService>.Instance);

        var newVariantId = Guid.Empty;
        await foreach (var streamEvent in service.RegenerateAsync(conversation.ConversationId, originalId).ConfigureAwait(false))
        {
            if (streamEvent.Type == ChatStreamEventTypes.AssistantPending)
            {
                newVariantId = streamEvent.MessageId;
            }
        }

        // The original turn's agent drove the resolve, NOT the conversation binding.
        await resolver.Received().ResolveAsync(originalTurnAgentId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await resolver.DidNotReceive().ResolveAsync(conversationAgentId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);

        // The regenerated variant is stamped with the FRESH (re-resolved) agent name + the agent id.
        var loaded = AssertEx.NotNull(await persistence.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var variant = loaded.Messages.Single(message => message.MessageId == newVariantId);
        AssertEx.Equal(originalTurnAgentId, variant.AgentDefinitionId);
        AssertEx.Equal("Renamed Original Agent", variant.AgentName);
    }

    [Test]
    public async Task RegenerateAsync_WhenBoundToOrchestrator_CarriesOrchestrationSpecOnPackage()
    {
        // Orchestrator hydration symmetry: a regenerated turn on a conversation bound to a Kind=Orchestrator definition must
        // carry the SAME orchestration spec a fresh send would — a missed hydration here would make reruns diverge.
        await using var provider = await BuildProviderAsync("regeneration-orchestrator.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
        var agentDefinitionId = Guid.NewGuid();

        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Orchestrated regen", "node", CreatedAtUtc: 10, AgentDefinitionId: agentDefinitionId))
                                            .ConfigureAwait(false);
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "what is 2+2?", CreatedAtUtc: 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, CreatedAtUtc: 12,
                             "model-x"))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(
                             new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 13, "four", Model: "model-x"))
                         .ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var runner = new RegenContextCapturingRunner(dispatcher);

        var store = Substitute.For<IAgentDefinitionStore>();
        store.GetByIdAsync(agentDefinitionId, Arg.Any<CancellationToken>()).Returns(CreateOrchestratorRecord(agentDefinitionId));
        var orchestrationResolver = Substitute.For<IOrchestrationResolver>();
        var spec = CreateSampleSpec();
        orchestrationResolver.ResolveAsync(Arg.Any<AgentDefinitionRecord>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                             .Returns(new ResolvedOrchestration(spec, "Orchestrator prompt.", "qwen3:8b", ReasoningEffort: null, AgentDefinitionVersion: 4));

        var service = new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), store, orchestrationResolver, CreateModelClassificationService(), CreateLocalModelProviderResolver(),
                CreateGgufModelCapabilityResolver(), Substitute.For<ICloudCredentialStore>(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            TimeProvider.System,
            NullLogger<NodeChatRegenerationService>.Instance);

        var drained = 0;
        await foreach (var _ in service.RegenerateAsync(conversation.ConversationId, originalId).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the regenerate to stream events.");
        AssertEx.NotNull(runner.LastOrchestrationSpec);
        AssertEx.Equal(spec.TriageParticipantKey, runner.LastOrchestrationSpec!.TriageParticipantKey);
        AssertEx.Equal(expected: 2, runner.LastOrchestrationSpec.Participants.Count);
    }

    [Test]
    public async Task RegenerateAsync_WhenConversationUnbound_RegeneratesWithDefaultPromptAndVersion()
    {
        // Parity with the stream-side unbound test (NodeChatStreamServiceTests): an unbound conversation must
        // regenerate with today's literals — the embedded LoadResolvedSystemPrompt and AgentDefinitionVersion 1 — and
        // the resolver must be consulted with a NULL binding (the default-persona contract).
        await using var provider = await BuildProviderAsync("regeneration-unbound.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Unbound regen", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        AssertEx.True(conversation.AgentDefinitionId is null, "The seeded conversation must be unbound.");

        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "what is 2+2?", CreatedAtUtc: 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, CreatedAtUtc: 12,
                             "model-x"))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(
                             new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 13, "four", Model: "model-x"))
                         .ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var runner = new RegenContextCapturingRunner(dispatcher);
        var resolver = CreateAgentDefinitionResolver();

        var service = new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelClassificationService(), CreateLocalModelProviderResolver(),
                CreateGgufModelCapabilityResolver(), Substitute.For<ICloudCredentialStore>(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            TimeProvider.System,
            NullLogger<NodeChatRegenerationService>.Instance);

        var drained = 0;
        await foreach (var _ in service.RegenerateAsync(conversation.ConversationId, originalId).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the regenerate to stream events.");
        await resolver.Received().ResolveAsync(agentDefinitionId: null, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, runner.LastAgentDefinitionVersion);
        AssertEx.NotNullOrEmpty(runner.LastSystemPrompt);
    }

    [Test]
    public async Task RegenerateAsync_WhenToolLifecycleReported_StreamsToolCallEvents()
    {
        await using var provider = await BuildProviderAsync("regeneration-tool-lifecycle.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        // Seed a completed turn: user question + a completed assistant answer (the "original" to regenerate).
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "what is the weather?", CreatedAtUtc: 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, CreatedAtUtc: 12,
                             "model-x"))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 13, "cloudy",
                             Model: "model-x"))
                         .ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var runner = new RegenToolEmittingRunner(dispatcher);
        var service = new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelClassificationService(), CreateLocalModelProviderResolver(),
                CreateGgufModelCapabilityResolver(), Substitute.For<ICloudCredentialStore>(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            TimeProvider.System,
            NullLogger<NodeChatRegenerationService>.Instance);

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.RegenerateAsync(conversation.ConversationId, originalId, useLocalTools: true).ConfigureAwait(false))
        {
            events.Add(streamEvent);
        }

        // Regenerate streams the tool-call lifecycle symmetric with the send path: requested then completed.
        var requested = events.Single(streamEvent => streamEvent.Type == ChatStreamEventTypes.ToolCallRequested);
        AssertEx.Equal("call-1", requested.ToolCallId);
        AssertEx.Equal("weather", requested.ToolName);
        AssertEx.Equal("{\"city\":\"berlin\"}", requested.Arguments);
        AssertEx.Equal(expected: false, requested.RequiresApproval);
        AssertEx.Equal(NodeChatMessageStatusValues.Streaming, requested.Status);

        var completed = events.Single(streamEvent => streamEvent.Type == ChatStreamEventTypes.ToolCallCompleted);
        AssertEx.Equal("call-1", completed.ToolCallId);
        AssertEx.Equal("weather", completed.ToolName);
        AssertEx.Equal("sunny", completed.Result);
        AssertEx.Equal(expected: false, completed.IsError);

        var requestedIndex = events.FindIndex(streamEvent => streamEvent.Type == ChatStreamEventTypes.ToolCallRequested);
        var completedIndex = events.FindIndex(streamEvent => streamEvent.Type == ChatStreamEventTypes.ToolCallCompleted);
        AssertEx.True(requestedIndex < completedIndex, "tool-call-requested must precede tool-call-completed.");
        AssertEx.True(events[requestedIndex].Sequence < events[completedIndex].Sequence, "Sequence must be monotonic.");
    }

    [Test]
    public async Task RegenerateAsync_WhenLocalToolsEnabled_OffersCatalogToolsInRuntimePackage()
    {
        await using var provider = await BuildProviderAsync("regeneration-offer-tools.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "what time is it?", CreatedAtUtc: 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, CreatedAtUtc: 12,
                             "model-x"))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(
                             new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 13, "noon", Model: "model-x"))
                         .ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var capturingRunner = new RegenContextCapturingRunner(dispatcher);
        var offerProvider = CreateOfferProvider(CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}"),
            CreateLocalToolDto("Calculate", "{\"type\":\"object\"}"));
        var service = new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelClassificationService(), CreateLocalModelProviderResolver(),
                CreateGgufModelCapabilityResolver(), Substitute.For<ICloudCredentialStore>(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            capturingRunner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = true
            }),
            StubNodeRuntimeSettings.Create().WithEnableTools(true).Build(),
            new NodeChatStreamCancellationRegistry(),
            offerProvider,
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            TimeProvider.System,
            NullLogger<NodeChatRegenerationService>.Instance);

        var drained = 0;
        await foreach (var _ in service.RegenerateAsync(conversation.ConversationId, originalId, useLocalTools: true).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the regenerate to stream events.");
        AssertEx.Equal(expected: 2, capturingRunner.LastAllowedTools.Count);
        AssertEx.Contains(capturingRunner.LastAllowedTools, tool => tool.Name == "GetCurrentTime");
        AssertEx.Contains(capturingRunner.LastAllowedTools, tool => tool.Name == "Calculate");
        foreach (var tool in capturingRunner.LastAllowedTools)
        {
            AssertEx.Equal(ToolLocation.ClientLocal, tool.Location);
            AssertEx.NotNullOrEmpty(tool.ParameterSchema);
        }
    }

    [Test]
    public async Task RegenerateAsync_WhenLocalToolsDisabled_OffersNoTools()
    {
        await using var provider = await BuildProviderAsync("regeneration-no-offer-tools.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "what time is it?", CreatedAtUtc: 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, CreatedAtUtc: 12,
                             "model-x"))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(
                             new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 13, "noon", Model: "model-x"))
                         .ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var capturingRunner = new RegenContextCapturingRunner(dispatcher);
        var offerProvider = CreateOfferProvider(CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}"),
            CreateLocalToolDto("Calculate", "{\"type\":\"object\"}"));
        var service = new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelClassificationService(), CreateLocalModelProviderResolver(),
                CreateGgufModelCapabilityResolver(), Substitute.For<ICloudCredentialStore>(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            capturingRunner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = true
            }),
            StubNodeRuntimeSettings.Create().WithEnableTools(true).Build(),
            new NodeChatStreamCancellationRegistry(),
            offerProvider,
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            TimeProvider.System,
            NullLogger<NodeChatRegenerationService>.Instance);

        var drained = 0;
        await foreach (var _ in service.RegenerateAsync(conversation.ConversationId, originalId, useLocalTools: false).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the regenerate to stream events.");
        AssertEx.Empty(capturingRunner.LastAllowedTools);
    }

    [Test]
    public async Task RegenerateAsync_OfVariant_ExcludesAllSiblingAssistantAnswersFromContext()
    {
        await using var provider = await BuildProviderAsync("regeneration-variant-context.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        // Seed: user question + completed original assistant answer.
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        var userMessageId = Guid.NewGuid();
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, userMessageId, "what is 2+2?", CreatedAtUtc: 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, CreatedAtUtc: 12,
                             "model-x"))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(
                             new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 13, "four", Model: "model-x"))
                         .ConfigureAwait(false);

        // First regenerate makes variant B (a sibling of the original whose parent is the ORIGINAL assistant turn).
        var dispatcher = new RegenRecordingDispatcher();
        var capturingRunner = new RegenContextCapturingRunner(dispatcher);
        var service = new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelClassificationService(), CreateLocalModelProviderResolver(),
                CreateGgufModelCapabilityResolver(), Substitute.For<ICloudCredentialStore>(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            capturingRunner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            TimeProvider.System,
            NullLogger<NodeChatRegenerationService>.Instance);

        ChatStreamEvent? firstCompleted = null;
        await foreach (var streamEvent in service.RegenerateAsync(conversation.ConversationId, originalId).ConfigureAwait(false))
        {
            if (streamEvent.Type == ChatStreamEventTypes.AssistantCompleted)
            {
                firstCompleted = streamEvent;
            }
        }

        AssertEx.True(firstCompleted is not null, "Expected the first regenerate to complete.");
        var variantBId = firstCompleted!.MessageId;

        // Now regenerate the VARIANT (B). Its parent_message_id points at the prior ASSISTANT answer, not the
        // user turn, so without a parent-walk the cutoff would include an assistant answer it should replace.
        var drained = 0;
        await foreach (var _ in service.RegenerateAsync(conversation.ConversationId, variantBId).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the variant regenerate to stream events.");

        // The context handed to the runner for the variant regenerate must contain ONLY the user turn — no
        // assistant answer (original or sibling variant) may leak in.
        var contextForVariant = AssertEx.NotNull(capturingRunner.LastContext);
        AssertEx.True(contextForVariant.All(m => m.Role == MessageRole.User), "No assistant answer may appear in a variant-regenerate context.");
        AssertEx.Equal(expected: 1, contextForVariant.Count);
        AssertEx.Equal(userMessageId, contextForVariant[0].Id);
    }

    [Test]
    public async Task RegenerateAsync_ThreadsReasoningEffortIntoRuntimePackage()
    {
        await using var provider = await BuildProviderAsync("regeneration-reasoning.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        // Seed a completed turn: user question + a completed assistant answer (the "original" to regenerate).
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "what is 2+2?", CreatedAtUtc: 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, CreatedAtUtc: 12,
                             "model-x"))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(
                             new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 13, "four", Model: "model-x"))
                         .ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var capturingRunner = new RegenContextCapturingRunner(dispatcher);
        var service = new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelClassificationService(), CreateLocalModelProviderResolver(),
                CreateGgufModelCapabilityResolver(), Substitute.For<ICloudCredentialStore>(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            capturingRunner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            TimeProvider.System,
            NullLogger<NodeChatRegenerationService>.Instance);

        var drained = 0;
        await foreach (var _ in service.RegenerateAsync(conversation.ConversationId, originalId, "high").ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the regenerate to stream events.");
        AssertEx.Equal("high", capturingRunner.LastReasoningEffort);
    }

    [Test]
    public async Task RegenerateAsync_WhenReasoningEffortOmitted_LeavesRuntimePackageReasoningNull()
    {
        await using var provider = await BuildProviderAsync("regeneration-reasoning-default.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "what is 2+2?", CreatedAtUtc: 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, CreatedAtUtc: 12,
                             "model-x"))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(
                             new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 13, "four", Model: "model-x"))
                         .ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var capturingRunner = new RegenContextCapturingRunner(dispatcher);
        var service = new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelClassificationService(), CreateLocalModelProviderResolver(),
                CreateGgufModelCapabilityResolver(), Substitute.For<ICloudCredentialStore>(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            capturingRunner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            TimeProvider.System,
            NullLogger<NodeChatRegenerationService>.Instance);

        var drained = 0;
        await foreach (var _ in service.RegenerateAsync(conversation.ConversationId, originalId).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the regenerate to stream events.");
        AssertEx.True(capturingRunner.LastContext is not null, "Expected the invocation runner to observe the package.");
        AssertEx.True(capturingRunner.LastReasoningEffort is null, "Expected reasoning effort to default to null.");
    }

    [Test]
    public async Task RegenerateAsync_WhenOriginRemote_ThrowsReadOnly()
    {
        await using var provider = await BuildProviderAsync("regeneration-remote.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        var conversationId = Guid.NewGuid();
        await persistence.EnsureConversationAsync(new NodeChatEnsureConversationRequest(conversationId, "Remote", "client-node", CreatedAtUtc: 10, NodeChatOriginValues.Remote)).ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var service = new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelClassificationService(), CreateLocalModelProviderResolver(),
                CreateGgufModelCapabilityResolver(), Substitute.For<ICloudCredentialStore>(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            new RegenCompletingRunner(dispatcher),
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            TimeProvider.System,
            NullLogger<NodeChatRegenerationService>.Instance);

        await AssertEx.ThrowsAsync<NodeChatReadOnlyConversationException>(async () =>
        {
            var drained = 0;
            await foreach (var _ in service.RegenerateAsync(conversationId, Guid.NewGuid()).ConfigureAwait(false))
            {
                drained++;
            }

            AssertEx.Equal(expected: 0, drained);
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task RegenerateAsync_WhenOriginalModelIsCodexCloudModel_OffersToolsAndSkipsOllamaClassification()
    {
        // Parity with NodeChatStreamServiceTests.SendMessageAsync_WhenActiveModelIsCodexCloudModel_*: regenerating a
        // turn whose original model is a Codex cloud id must resolve capabilities from the Codex matrix (thinking on +
        // tools per V0=true), NOT the Ollama /api/show classification — so the classifier is never consulted for the
        // Codex id, and the requested local tool offer IS honored (regression guard for the regen-path Codex gate).
        await using var provider = await BuildProviderAsync("regeneration-codex-capabilities.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        const string CodexModel = "gpt-5.5";
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Codex regen", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "what is 2+2?", CreatedAtUtc: 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        // The original assistant turn was produced by the Codex cloud model, so the regenerate resolves that model.
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, CreatedAtUtc: 12,
                             CodexModel))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 13, "four",
                             Model: CodexModel))
                         .ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var runner = new RegenCompletingRunner(dispatcher);
        var offerProvider = CreateOfferProvider();
        var classificationService = CreateModelClassificationService();
        var service = new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(), classificationService, CreateLocalModelProviderResolver(),
                CreateGgufModelCapabilityResolver(), Substitute.For<ICloudCredentialStore>(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = true
            }),
            StubNodeRuntimeSettings.Create().WithEnableTools(true).Build(),
            new NodeChatStreamCancellationRegistry(),
            offerProvider,
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            TimeProvider.System,
            NullLogger<NodeChatRegenerationService>.Instance);

        var drained = 0;
        await foreach (var _ in service.RegenerateAsync(conversation.ConversationId, originalId, useLocalTools: true).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the regenerate to stream events.");

        // The Ollama classifier is never consulted for a Codex model id — capabilities come from the Codex matrix.
        await classificationService.DidNotReceive()
                                   .ClassifyAsync(
                                       Arg.Is<IEnumerable<(string ModelName, string? Digest)>>(models => models.Any(m => string.Equals(m.ModelName, CodexModel, StringComparison.OrdinalIgnoreCase))),
                                       Arg.Any<CancellationToken>())
                                   .ConfigureAwait(false);

        // Tool calling is enabled for all Codex ids (V0=true), so the requested local tool offer is honored on
        // regenerate. It is requested with isCloudModel: true so the knowledge-tool provider-locality gate applies.
        offerProvider.Received().GetOfferedTools(CodexModel, true);
    }

    private async Task<ServiceProvider> BuildProviderAsync(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, fileName);
        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<NodeChatPersistenceWriter>();

        var provider = services.BuildServiceProvider(true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

        return provider;
    }

    private static ILocalToolOfferProvider CreateOfferProvider(params AllowedToolDto[] tools)
    {
        var provider = Substitute.For<ILocalToolOfferProvider>();
        provider.GetOfferedTools(Arg.Any<string?>(), Arg.Any<bool>()).Returns(tools);
        return provider;
    }

    // The default node-settings store: no operator-selected node default, so model resolution falls through to the
    // original turn's model (or the static config fallback) exactly as the send path does.
    private static INodeSettingsStore CreateNodeSettingsStore(string? defaultModelName = null)
    {
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings
        {
            DefaultModelName = defaultModelName
        });
        return store;
    }

    // The default classification service: every model resolves to BOTH thinking- and tools-capable, so the existing
    // think/tool-offer assertions stay byte-identical (these tests pre-date per-model capability gating).
    private static IModelClassificationService CreateModelClassificationService()
    {
        var service = Substitute.For<IModelClassificationService>();
        service.ClassifyAsync(Arg.Any<IEnumerable<(string ModelName, string? Digest)>>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var models = callInfo.Arg<IEnumerable<(string ModelName, string? Digest)>>();
                   var map = new Dictionary<string, ModelClassificationResult>(StringComparer.OrdinalIgnoreCase);
                   foreach (var (modelName, _) in models)
                   {
                       if (!string.IsNullOrWhiteSpace(modelName) && !map.ContainsKey(modelName))
                       {
                           map[modelName] = new ModelClassificationResult(modelName, ModelKind.Chat, ModelKind.Chat, ["completion", "tools", "thinking"], IsOverridden: false);
                       }
                   }

                   return Task.FromResult<IReadOnlyDictionary<string, ModelClassificationResult>>(map);
               });
        return service;
    }

    // The default resolver reports every model as not-a-GGUF (null), so these Ollama-routed regen tests keep their
    // /api/show classification behavior. A llama.cpp-capability test overrides TryResolveAsync explicitly.
    private static IGgufModelCapabilityResolver CreateGgufModelCapabilityResolver(GgufModelCapabilities? capabilities = null)
    {
        var resolver = Substitute.For<IGgufModelCapabilityResolver>();
        resolver.TryResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(capabilities);
        return resolver;
    }

    // Reports the active model as an Ollama-routed model so the shared ChatTurnResolver's capability gate classifies via
    // the model-classification service (these regen tests' GGUF stub resolves null), matching the pre-extraction path.
    private static ILocalModelProviderResolver CreateLocalModelProviderResolver()
    {
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(OllamaLocalModelProvider.OllamaProviderName);
        return resolver;
    }

    // No-op extraction dispatcher: these tests are not about post-run memory (the playbook-disabled default agent never
    // fires the hook). A substitute's void Dispatch is a no-op, keeping the regenerate SSE assertions intact.
    private static IMemoryExtractionDispatcher CreateMemoryExtractionDispatcher()
    {
        return Substitute.For<IMemoryExtractionDispatcher>();
    }

    // The default local-default resolver resolves to an installed GGUF chat model so a regenerate of a "Local runtime
    // default" turn proceeds. Regenerate only consults it when the ORIGINAL turn carried no explicit model; the
    // existing regen tests seed an explicit original model, so this is inert for them. It echoes the persisted node
    // default when set and otherwise falls back to the static config model. The no-model test passes resolved=null +
    // echoPersistedDefault=false to force the empty result.
    private static ILocalDefaultChatModelResolver CreateLocalDefaultChatModelResolver(string? resolved = null, bool echoPersistedDefault = true)
    {
        var fallback = resolved ?? new LocalChatAgentOptions().DefaultModel;
        var resolver = Substitute.For<ILocalDefaultChatModelResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    if (!echoPersistedDefault)
                    {
                        return Task.FromResult<string?>(resolved);
                    }

                    var persistedDefault = callInfo.Arg<string?>();
                    return Task.FromResult<string?>(string.IsNullOrWhiteSpace(persistedDefault) ? fallback : persistedDefault);
                });
        return resolver;
    }

    // The default (unbound) resolver: ResolveAsync returns null, so the regeneration path keeps today's literals —
    // these tests exercise the default chat persona. Bound-agent behavior is covered by the dedicated bound tests.
    private static IAgentDefinitionResolver CreateAgentDefinitionResolver()
    {
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns((ResolvedAgentRuntime?)null);
        return resolver;
    }

    // The default-agent provider: returns null so the effective-agent precedence falls through to a null id (no seeded
    // Default Assistant in these unit tests), keeping the default-persona contract on the rerun.
    private static IDefaultAgentProvider CreateDefaultAgentProvider(Guid? defaultAgentId = null)
    {
        var provider = Substitute.For<IDefaultAgentProvider>();
        provider.GetDefaultAgentIdAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(defaultAgentId));
        return provider;
    }

    // The default store/orchestration resolver: GetByIdAsync returns null so ResolveOrchestrationAsync never reaches the
    // orchestration resolver — the package carries no spec and the single-agent regeneration path is byte-identical.
    private static IAgentDefinitionStore CreateAgentDefinitionStore()
    {
        var store = Substitute.For<IAgentDefinitionStore>();
        store.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((AgentDefinitionRecord?)null);
        return store;
    }

    private static IOrchestrationResolver CreateOrchestrationResolver()
    {
        var resolver = Substitute.For<IOrchestrationResolver>();
        resolver.ResolveAsync(Arg.Any<AgentDefinitionRecord>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns((ResolvedOrchestration?)null);
        return resolver;
    }

    private static AgentDefinitionRecord CreateOrchestratorRecord(Guid id)
    {
        return new AgentDefinitionRecord(id,
            "Orchestrator",
            Description: null,
            "Orchestrator prompt.",
            "qwen3:8b",
            ReasoningEffort: null,
            AgentDefinitionKind.Orchestrator,
            [],
            new Dictionary<string, bool>(),
            OrchestrationTopologyJson: null,
            Version: 4,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }

    private static OrchestrationSpec CreateSampleSpec()
    {
        return new OrchestrationSpec
        {
            TriageParticipantKey = "a",
            MaxTurnsPerAgent = 6,
            ReturnToPrevious = false,
            Participants =
            [
                new OrchestrationSpecParticipant
                {
                    Key = "a",
                    Name = "Triage",
                    Instructions = "Triage.",
                    ModelId = "qwen3:8b",
                    Tools = []
                },
                new OrchestrationSpecParticipant
                {
                    Key = "b",
                    Name = "Specialist",
                    Instructions = "Specialist.",
                    ModelId = "qwen3:8b",
                    Tools = []
                }
            ],
            Edges =
            [
                new OrchestrationSpecEdge
                {
                    FromKey = "a",
                    ToKey = "b"
                }
            ]
        };
    }

    private static AllowedToolDto CreateLocalToolDto(string name, string parameterSchema)
    {
        return new AllowedToolDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = ToolLocation.ClientLocal,
            ParameterSchema = parameterSchema,
            RequiresApproval = false
        };
    }

    private sealed class RegenCompletingRunner(RegenRecordingDispatcher dispatcher) : IInvocationRunner
    {
        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            await dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, "regenerated answer").ConfigureAwait(false);
            await dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId, inputTokens: 5, outputTokens: 2, totalTokens: 7, reasoningTokens: 0).ConfigureAwait(false);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(string.Empty);
        }

        public void Cancel(Guid invocationId)
        {
        }

        public void CancelAll()
        {
        }

        public void CleanupStaleToolCalls(TimeSpan maxAge)
        {
        }

        public void ResolveApprovalResult(ApprovalResolvedEvent evt)
        {
        }

        public void ResolveToolCallResult(ToolCallResultEvent evt)
        {
        }
    }

    private sealed class RegenContextCapturingRunner(RegenRecordingDispatcher dispatcher) : IInvocationRunner
    {
        // The conversation context handed to the most recent invocation; the test asserts the variant
        // regenerate never includes a sibling assistant answer.
        public IReadOnlyList<ConversationMessageDto>? LastContext { get; private set; }

        // The reasoning effort carried on the runtime package; the test asserts the regenerate honors the
        // current reasoning selection threaded from the hub.
        public string? LastReasoningEffort { get; private set; }

        // The offer list carried on the runtime package; the test asserts the local tool catalog reaches the
        // runtime package on regenerate only when the client opted in.
        public IReadOnlyList<AllowedToolDto> LastAllowedTools { get; private set; } = [];

        // The system prompt and agent-definition version carried on the runtime package; the binding test asserts a
        // bound definition's persona + version reach the regenerate path (a missed hydration site = divergent reruns).
        public string? LastSystemPrompt { get; private set; }
        public int LastAgentDefinitionVersion { get; private set; }

        // The model the runtime package ran on; the effective-model tests assert the dropdown pick / pin precedence.
        public string? LastModelProfile { get; private set; }
        public OrchestrationSpec? LastOrchestrationSpec { get; private set; }
        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context.Package.ConversationContext;
            LastReasoningEffort = context.Package.ReasoningEffort;
            LastAllowedTools = context.Package.AllowedTools;
            LastSystemPrompt = context.Package.ResolvedSystemPrompt;
            LastAgentDefinitionVersion = context.Package.AgentDefinitionVersion;
            LastModelProfile = context.Package.ModelProfile;
            LastOrchestrationSpec = context.Package.OrchestrationSpec;
            await dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, "regenerated answer").ConfigureAwait(false);
            await dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId, inputTokens: 5, outputTokens: 2, totalTokens: 7, reasoningTokens: 0).ConfigureAwait(false);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(string.Empty);
        }

        public void Cancel(Guid invocationId)
        {
        }

        public void CancelAll()
        {
        }

        public void CleanupStaleToolCalls(TimeSpan maxAge)
        {
        }

        public void ResolveApprovalResult(ApprovalResolvedEvent evt)
        {
        }

        public void ResolveToolCallResult(ToolCallResultEvent evt)
        {
        }
    }

    private sealed class RegenToolEmittingRunner(RegenRecordingDispatcher dispatcher) : IInvocationRunner
    {
        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            await dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
            {
                InvocationId = context.Package.InvocationId,
                ToolCallId = "call-1",
                ToolName = "weather",
                Phase = ToolCallLifecyclePhase.Requested,
                Arguments = "{\"city\":\"berlin\"}",
                RequiresApproval = false
            }).ConfigureAwait(false);
            await dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
            {
                InvocationId = context.Package.InvocationId,
                ToolCallId = "call-1",
                ToolName = "weather",
                Phase = ToolCallLifecyclePhase.Completed,
                Result = "sunny",
                IsError = false
            }).ConfigureAwait(false);
            await dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, "regenerated answer").ConfigureAwait(false);
            await dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId, inputTokens: 5, outputTokens: 2, totalTokens: 7, reasoningTokens: 0).ConfigureAwait(false);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(string.Empty);
        }

        public void Cancel(Guid invocationId)
        {
        }

        public void CancelAll()
        {
        }

        public void CleanupStaleToolCalls(TimeSpan maxAge)
        {
        }

        public void ResolveApprovalResult(ApprovalResolvedEvent evt)
        {
        }

        public void ResolveToolCallResult(ToolCallResultEvent evt)
        {
        }
    }

    private sealed class RegenRecordingDispatcher : IWorkerEventDispatcher
    {
        public event EventHandler<InvocationStateChangedEventArgs>? InvocationStateChanged;

        public event EventHandler<ToolCallLifecycleChangedEventArgs>? ToolCallLifecycleChanged;

        public event EventHandler<TurnNoticeChangedEventArgs>? TurnNoticeChanged;

        public InvocationState? CurrentInvocation { get; private set; }

        public bool IsAcceptingRemoteInvocations => true;

        public void StopAcceptingRemoteInvocations()
        {
        }

        public Task DispatchInvocationAssignedAsync(EncryptedRuntimePackageDto package)
        {
            return Task.CompletedTask;
        }

        public Task DispatchInvocationAssignedV2Async(InvocationAssignedEnvelope envelope)
        {
            return Task.CompletedTask;
        }

        public Task DispatchToolCallResultAsync(ToolCallResultEvent evt)
        {
            return Task.CompletedTask;
        }

        public Task DispatchDisconnectRequestedAsync(DisconnectRequestedEvent evt)
        {
            return Task.CompletedTask;
        }

        public Task DispatchApprovalResolvedAsync(ApprovalResolvedEvent evt)
        {
            return Task.CompletedTask;
        }

        public Task DispatchInvocationCancelledAsync(InvocationCancelledEvent evt)
        {
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> ReportInvocationAssignedAsync(RuntimePackage package, CancellationToken cancellationToken = default)
        {
            CurrentInvocation = new InvocationState
            {
                InvocationId = package.InvocationId,
                ConversationId = package.ConversationId,
                Status = InvocationStatus.Assigned,
                StartedAt = DateTimeOffset.UtcNow,
                LastUpdatedAt = DateTimeOffset.UtcNow
            };
            RaiseChanged();
            return Task.FromResult<IAsyncDisposable>(NoopLease.Instance);
        }

        public Task ReportInvocationStreamChunkAsync(Guid invocationId, string chunk)
        {
            if (CurrentInvocation is null)
            {
                return Task.CompletedTask;
            }

            CurrentInvocation.Status = InvocationStatus.Running;
            CurrentInvocation.StreamedContent += chunk;
            CurrentInvocation.StreamedChunkCount++;
            CurrentInvocation.LastUpdatedAt = DateTimeOffset.UtcNow;
            RaiseChanged();
            return Task.CompletedTask;
        }

        public Task ReportInvocationThinkingChunkAsync(Guid invocationId, string chunk)
        {
            if (CurrentInvocation is null)
            {
                return Task.CompletedTask;
            }

            CurrentInvocation.Status = InvocationStatus.Running;
            CurrentInvocation.StreamedThinkingContent += chunk;
            CurrentInvocation.StreamedThinkingChunkCount++;
            CurrentInvocation.LastUpdatedAt = DateTimeOffset.UtcNow;
            RaiseChanged();
            return Task.CompletedTask;
        }

        public Task ReportInvocationCompletedAsync(Guid invocationId, int? inputTokens = null, int? outputTokens = null, int? totalTokens = null, int? reasoningTokens = null,
            long? generationDurationMs = null)
        {
            if (CurrentInvocation is not null)
            {
                CurrentInvocation.Status = InvocationStatus.Completed;
                CurrentInvocation.CompletedAt = DateTimeOffset.UtcNow;
                CurrentInvocation.InputTokens = inputTokens;
                CurrentInvocation.OutputTokens = outputTokens;
                CurrentInvocation.TotalTokens = totalTokens;
                CurrentInvocation.ReasoningTokens = reasoningTokens;
                CurrentInvocation.GenerationDurationMs = generationDurationMs;
                RaiseChanged();
            }

            return Task.CompletedTask;
        }

        public Task ReportInvocationFailedAsync(Guid invocationId, string failureMessage, FailureCategory failureCategory)
        {
            if (CurrentInvocation is not null)
            {
                CurrentInvocation.Status = failureCategory == FailureCategory.Cancelled ? InvocationStatus.Cancelled : InvocationStatus.Failed;
                CurrentInvocation.Error = failureMessage;
                CurrentInvocation.FailureCategory = failureCategory;
                CurrentInvocation.CompletedAt = DateTimeOffset.UtcNow;
                RaiseChanged();
            }

            return Task.CompletedTask;
        }

        public Task ReportToolCallRequestedAsync(ToolCallRequestPayload payload)
        {
            return Task.CompletedTask;
        }

        public Task ReportApprovalRequestedAsync(ApprovalRequestPayload payload)
        {
            return Task.CompletedTask;
        }

        public Task ReportToolCallLifecycleAsync(ToolCallLifecyclePayload payload)
        {
            ToolCallLifecycleChanged?.Invoke(this, new ToolCallLifecycleChangedEventArgs(payload));
            return Task.CompletedTask;
        }

        public Task ReportTurnNoticeAsync(TurnNoticePayload payload)
        {
            TurnNoticeChanged?.Invoke(this, new TurnNoticeChangedEventArgs(payload));
            return Task.CompletedTask;
        }

        private void RaiseChanged()
        {
            InvocationStateChanged?.Invoke(this, new InvocationStateChangedEventArgs(CurrentInvocation!));
        }

        private sealed class NoopLease : IAsyncDisposable
        {
            public static readonly NoopLease Instance = new();

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }
}
