namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
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
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Knowledge;
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
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
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
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
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
    public async Task RegenerateAsync_FansAPendingApprovalOutToTheStream()
    {
        // The regenerate path subscribed four dispatcher events and not ApprovalRequestedChanged, so a regenerated turn
        // that called an approval-gated tool parked with no Approve/Deny card ever reaching the browser — the run then
        // sat until its timeout. Same defect the ask_user subscription in this file was added to fix, sibling event.
        await using var provider = await BuildProviderAsync("regeneration-approval.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
        var (conversationId, originalId) = await SeedRegeneratableTurnAsync(persistence).ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var otherInvocationId = Guid.NewGuid();
        var runner = new RegenCompletingRunner(dispatcher,
            async invocationId =>
            {
                await dispatcher.ReportApprovalLifecycleAsync(new ApprovalLifecyclePayload
                                {
                                    InvocationId = invocationId,
                                    RequestId = "approval-1",
                                    CallId = "call-1",
                                    ToolName = "run_command",
                                    Description = "Run a command"
                                })
                                .ConfigureAwait(false);

                // A concurrent turn's approval must not leak into this stream: the handler filters on the run's own
                // request id, exactly as the other four do.
                await dispatcher.ReportApprovalLifecycleAsync(new ApprovalLifecyclePayload
                                {
                                    InvocationId = otherInvocationId,
                                    RequestId = "approval-other",
                                    CallId = "call-9",
                                    ToolName = "run_command",
                                    Description = "Run a command for another turn"
                                })
                                .ConfigureAwait(false);
            });

        var service = CreateService(persistence, dispatcher, runner);

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.RegenerateAsync(conversationId, originalId).ConfigureAwait(false))
        {
            events.Add(streamEvent);
        }

        var approval = AssertEx.NotNull(events.SingleOrDefault(streamEvent => streamEvent.Type == ChatStreamEventTypes.ApprovalRequested));
        AssertEx.Equal("approval-1", approval.ApprovalRequestId);
        AssertEx.Equal("call-1", approval.ToolCallId);
        AssertEx.Equal("run_command", approval.ToolName);

        // The card is ordered before the terminal, so the browser can render it while the turn is still parked.
        AssertEx.True(approval.Sequence < events.Single(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantCompleted).Sequence,
            "The approval must be sequenced before the terminal.");

        AssertEx.False(dispatcher.HasApprovalSubscribers, "The approval handler must be detached once the stream ends.");
    }

    [Test]
    public async Task RegenerateAsync_WhenTheTurnThrowsBeforeItsTasksExist_StillDetachesTheApprovalHandler()
    {
        // The handlers are attached before the first await inside the try, so a throw from the pre-task setup (a
        // client disconnect during GetEnableToolsAsync, a settings failure) must still reach the finally. Leaving them
        // attached leaks a handler on the singleton dispatcher for the whole process lifetime.
        await using var provider = await BuildProviderAsync("regeneration-approval-throw.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
        var (conversationId, originalId) = await SeedRegeneratableTurnAsync(persistence).ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var runtimeSettings = StubNodeRuntimeSettings.Create().Build();
        runtimeSettings.GetEnableToolsAsync(Arg.Any<CancellationToken>())
                       .Returns<Task<bool>>(_ => throw new InvalidOperationException("node settings unavailable"));

        var service = CreateService(persistence, dispatcher, new RegenCompletingRunner(dispatcher), runtimeSettings);

        await AssertEx.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in service.RegenerateAsync(conversationId, originalId).ConfigureAwait(false))
            {
                // The throw lands before the pump/runner tasks are created, so only the pre-run lifecycle events flow.
            }
        }).ConfigureAwait(false);

        AssertEx.False(dispatcher.HasApprovalSubscribers, "The approval handler must be detached even when the turn throws before its tasks exist.");
    }

    // Seeds a completed user + assistant turn and returns the ids needed to regenerate that assistant answer.
    private static async Task<(Guid ConversationId, Guid OriginalMessageId)> SeedRegeneratableTurnAsync(NodeChatPersistenceService persistence)
    {
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

        return (conversation.ConversationId, originalId);
    }

    private static NodeChatRegenerationService CreateService(NodeChatPersistenceService persistence,
        RegenRecordingDispatcher dispatcher,
        IInvocationRunner runner,
        INodeRuntimeSettings? runtimeSettings = null)
    {
        return new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            runtimeSettings ?? StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            NullLogger<NodeChatRegenerationService>.Instance);
    }

    [Test]
    public async Task RegenerateAsync_WhenPlainChatWithKnowledgeBase_GroundsAndRecordsSources()
    {
        // Parity with the send path (NodeChatStreamServiceTests): an opt-in regenerate retrieves KB hits,
        // inlines them as ONE fenced untrusted context block, and records their provenance as the variant's sources.
        await using var provider = await BuildProviderAsync("regeneration-kb-grounds.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
        var (conversation, originalId) = await SeedCompletedOriginalAsync(persistence).ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var runner = new RegenContextCapturingRunner(dispatcher);
        var scopeFactory = CreateKnowledgeScopeFactory(KnowledgeHit("Runbook", "Restart the service with the eject command.", score: 0.91));
        var service = CreateServiceWithScopeFactory(persistence, runner, dispatcher, scopeFactory);

        ChatStreamEvent? completed = null;
        await foreach (var streamEvent in service.RegenerateAsync(conversation.ConversationId, originalId, reasoningEffort: null, useLocalTools: false, useKnowledgeBase: true)
                                                 .ConfigureAwait(false))
        {
            if (streamEvent.Type == ChatStreamEventTypes.AssistantCompleted)
            {
                completed = streamEvent;
            }
        }

        // The fenced KB block reached the rerun's context, ahead of the conversation history.
        var context = AssertEx.NotNull(runner.LastContext);
        AssertEx.Contains(context, message => message.Content.Contains(KnowledgeChatContextComposer.Preamble, StringComparison.Ordinal));
        AssertEx.Contains(context, message => message.Content.Contains("Restart the service with the eject command.", StringComparison.Ordinal));

        // The inlined hits' provenance is persisted on the regenerated variant's metadata (the sources strip).
        var variantId = AssertEx.NotNull(completed).MessageId;
        var reloaded = await persistence.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false);
        var variant = AssertEx.NotNull(reloaded).Messages.Single(message => message.MessageId == variantId);
        var sources = AssertEx.NotNull(variant.Sources);
        AssertEx.Equal(expected: 1, sources.Count);
        AssertEx.Equal("Runbook", sources[0].Title);
    }

    [Test]
    public async Task RegenerateAsync_WhenKnowledgeBaseRetrievalEmpty_ProceedsWithoutContextOrSources()
    {
        // Degrade gracefully (mirrors the send path): no matching chunks means no KB context and no sources on the
        // regenerated variant, but the rerun still completes.
        await using var provider = await BuildProviderAsync("regeneration-kb-empty.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
        var (conversation, originalId) = await SeedCompletedOriginalAsync(persistence).ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var runner = new RegenContextCapturingRunner(dispatcher);
        var scopeFactory = CreateKnowledgeScopeFactory();
        var service = CreateServiceWithScopeFactory(persistence, runner, dispatcher, scopeFactory);

        ChatStreamEvent? completed = null;
        await foreach (var streamEvent in service.RegenerateAsync(conversation.ConversationId, originalId, reasoningEffort: null, useLocalTools: false, useKnowledgeBase: true)
                                                 .ConfigureAwait(false))
        {
            if (streamEvent.Type == ChatStreamEventTypes.AssistantCompleted)
            {
                completed = streamEvent;
            }
        }

        var context = AssertEx.NotNull(runner.LastContext);
        AssertEx.False(context.Any(message => message.Content.Contains(KnowledgeChatContextComposer.Preamble, StringComparison.Ordinal)),
            "no KB context block must be composed when retrieval returns nothing.");
        var variantId = AssertEx.NotNull(completed).MessageId;
        var reloaded = await persistence.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false);
        var variant = AssertEx.NotNull(reloaded).Messages.Single(message => message.MessageId == variantId);
        AssertEx.Null(variant.Sources);
    }

    [Test]
    public async Task RegenerateAsync_WhenCloudEffectiveModelWithKnowledgeBase_WithholdsAndNotifiesByDefault()
    {
        // The KB egress gate mirrors the send path: a cloud effective model must NOT receive KB context without the
        // operator opt-in. The rerun runs without KB context and the user gets a KnowledgeWithheld notice.
        await using var provider = await BuildProviderAsync("regeneration-kb-cloud.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        // The original assistant turn was produced by a Codex cloud model, so the regenerate resolves that cloud model.
        const string CloudModel = "gpt-5.5";
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "how do I restart the service?", CreatedAtUtc: 11))
                         .ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, CreatedAtUtc: 12,
                             CloudModel))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 13, "eject it",
                             Model: CloudModel))
                         .ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var runner = new RegenContextCapturingRunner(dispatcher);
        var scopeFactory = CreateKnowledgeScopeFactory(KnowledgeHit("Runbook", "secret runbook body", score: 0.9));
        var service = CreateServiceWithScopeFactory(persistence, runner, dispatcher, scopeFactory, allowCloudModelAccess: false);

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.RegenerateAsync(conversation.ConversationId, originalId, reasoningEffort: null, useLocalTools: false, useKnowledgeBase: true)
                                                 .ConfigureAwait(false))
        {
            events.Add(streamEvent);
        }

        var context = AssertEx.NotNull(runner.LastContext);
        AssertEx.False(context.Any(message => message.Content.Contains(KnowledgeChatContextComposer.Preamble, StringComparison.Ordinal)),
            "the KB context block must not be composed for a cloud model without opt-in.");
        AssertEx.False(context.Any(message => message.Content.Contains("secret runbook body", StringComparison.Ordinal)),
            "a cloud model must not receive KB content without opt-in.");
        AssertEx.Contains(events, streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantNotice
                                                 && streamEvent.NoticeKind == nameof(TurnNoticeKind.KnowledgeWithheld));
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
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
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
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
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
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
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
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
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
        await resolver.Received().ResolveAsync(agentDefinitionId, Arg.Any<string?>(), Arg.Is<string?>(query => query == "what is 2+2?"), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
                          Arg.Any<CancellationToken>())
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
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
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
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
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
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
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
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
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
        await resolver.Received().ResolveAsync(originalTurnAgentId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                      .ConfigureAwait(false);
        await resolver.DidNotReceive().ResolveAsync(conversationAgentId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                      .ConfigureAwait(false);

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
        // The chat-turn resolver now gates the orchestration reload on the resolved runtime's Kind (it reuses the
        // definition the resolver already loaded), so the resolver must surface Kind=Orchestrator for this bound agent.
        var agentDefinitionResolver = Substitute.For<IAgentDefinitionResolver>();
        agentDefinitionResolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                               .Returns(new ResolvedAgentRuntime("Orchestrator persona.", [], ModelProfile: null, ReasoningEffort: null, AgentDefinitionVersion: 4,
                                   agentDefinitionId, "Orchestrator", Kind: AgentDefinitionKind.Orchestrator));
        var orchestrationResolver = Substitute.For<IOrchestrationResolver>();
        var spec = CreateSampleSpec();
        orchestrationResolver.ResolveAsync(Arg.Any<AgentDefinitionRecord>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                             .Returns(OrchestrationResolution.Compiled(new ResolvedOrchestration(spec, "Orchestrator prompt.", "qwen3:8b", ReasoningEffort: null, AgentDefinitionVersion: 4,
                                 AnyParticipantIsCloud: false, FirstCloudParticipantModel: null)));

        var service = new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(agentDefinitionResolver, store, orchestrationResolver, CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
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
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
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
    public async Task RegenerateAsync_WhenOrchestrationDegrades_EmitsNoticeNamingTheReason()
    {
        // Send/regenerate parity: a rerun whose orchestration does not compile must tell the operator why, exactly as
        // the send path does — otherwise the same silent degrade reappears on every regenerate.
        await using var provider = await BuildProviderAsync("regeneration-orchestrator-degraded.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
        var agentDefinitionId = Guid.NewGuid();

        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Degraded regen", "node", CreatedAtUtc: 10, AgentDefinitionId: agentDefinitionId))
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
        var agentDefinitionResolver = Substitute.For<IAgentDefinitionResolver>();
        agentDefinitionResolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                               .Returns(new ResolvedAgentRuntime("Orchestrator persona.", [], ModelProfile: null, ReasoningEffort: null, AgentDefinitionVersion: 4,
                                   agentDefinitionId, "Orchestrator", Kind: AgentDefinitionKind.Orchestrator));
        var orchestrationResolver = Substitute.For<IOrchestrationResolver>();
        orchestrationResolver.ResolveAsync(Arg.Any<AgentDefinitionRecord>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                             .Returns(OrchestrationResolution.Degraded(OrchestrationDegradationReason.ModelNotToolCapable, "the model for this turn cannot call tools"));

        var service = new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(agentDefinitionResolver, store, orchestrationResolver, CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
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
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            NullLogger<NodeChatRegenerationService>.Instance);

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.RegenerateAsync(conversation.ConversationId, originalId).ConfigureAwait(false))
        {
            events.Add(streamEvent);
        }

        AssertEx.True(runner.LastOrchestrationSpec is null, "a degraded resolution must leave the rerun single-agent.");
        var notice = events.First(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.OrchestrationDegraded));
        AssertEx.Contains(notice.NoticeMessage, "the model for this turn cannot call tools");
        AssertEx.Contains(notice.NoticeMessage, "ran as a single agent");
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
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
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
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            NullLogger<NodeChatRegenerationService>.Instance);

        var drained = 0;
        await foreach (var _ in service.RegenerateAsync(conversation.ConversationId, originalId).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the regenerate to stream events.");
        await resolver.Received().ResolveAsync(agentDefinitionId: null, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                      .ConfigureAwait(false);
        AssertEx.Equal(expected: 1, runner.LastAgentDefinitionVersion);
        AssertEx.NotNullOrEmpty(runner.LastSystemPrompt);
    }

    [Test]
    public async Task RegenerateAsync_WhenToolLifecycleReported_StreamsToolCallEvents()
    {
        await using var provider = await BuildProviderAsync("regeneration-tool-lifecycle.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

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
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
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
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
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
    public async Task RegenerateAsync_AppliesOperatorMaxMessageRequestTimeoutToRuntimePackage()
    {
        // Send-path parity: the operator's node-level "Maximum message request timeout" (900s here) must bound a
        // regenerated turn too. Before the fix the package carried a null timeout block and the builder's own default
        // cut long reruns off with FailureCategory=Timeout. 900 is deliberately NOT the default, so this still fails
        // if the wiring is dropped. Tool-call/stream-idle keep their defaults.
        await using var provider = await BuildProviderAsync("regeneration-node-timeout.sqlite").ConfigureAwait(false);
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
        var service = new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            capturingRunner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(maxMessageRequestTimeoutSeconds: 900),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            NullLogger<NodeChatRegenerationService>.Instance);

        var streamed = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.RegenerateAsync(conversation.ConversationId, originalId).ConfigureAwait(false))
        {
            streamed.Add(streamEvent);
        }

        AssertEx.True(streamed.Count > 0, "Expected the regenerate to stream events.");
        AssertEx.NotNull(capturingRunner.LastTimeouts);
        AssertEx.Equal(expected: 900, capturingRunner.LastTimeouts!.InvocationTimeoutSeconds);
        AssertEx.Equal(expected: 30, capturingRunner.LastTimeouts.ToolCallTimeoutSeconds);
        AssertEx.Equal(expected: 60, capturingRunner.LastTimeouts.StreamIdleTimeoutSeconds);

        // Send-path parity for the browser's stream watchdog: the queued + streaming events carry the same ceiling the
        // package runs under, so a regenerated turn's client-side deadline is derived, not a fixed constant.
        AssertEx.ContainsSingle(streamed,
            streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantQueued && streamEvent.InvocationTimeoutSeconds == 900);
        AssertEx.ContainsSingle(streamed,
            streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantStreaming && streamEvent.InvocationTimeoutSeconds == 900);
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
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
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
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
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
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
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
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
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
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
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
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
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

    /// <summary>
    ///     Regenerating an EARLY turn AFTER later turns exist mints a sibling whose PHYSICAL sequence lands past those
    ///     later turns. Selecting it and then regenerating a LATER turn must keep that answer in context at the early
    ///     position: the cutoff filter runs in anchor space, so a raw `Sequence &lt;= cutoff` comparison would drop the
    ///     first exchange's answer from the rerun entirely.
    /// </summary>
    [Test]
    public async Task RegenerateAsync_WhenALateMintedSiblingOfAnEarlyTurnIsSelected_KeepsItInContextAtTheEarlyPosition()
    {
        await using var provider = await BuildProviderAsync("regeneration-late-sibling-anchor.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        var conversationId = conversation.ConversationId;

        // U1 A1 U2 A2 U3 A3.
        var userOneId = await SeedUserTurnAsync(persistence, conversationId, "u-one", createdAtUtc: 11).ConfigureAwait(false);
        var answerOneId = await SeedAssistantTurnAsync(persistence, conversationId, "a-one", createdAtUtc: 12).ConfigureAwait(false);
        var userTwoId = await SeedUserTurnAsync(persistence, conversationId, "u-two", createdAtUtc: 13).ConfigureAwait(false);
        var answerTwoId = await SeedAssistantTurnAsync(persistence, conversationId, "a-two", createdAtUtc: 14).ConfigureAwait(false);
        var userThreeId = await SeedUserTurnAsync(persistence, conversationId, "u-three", createdAtUtc: 15).ConfigureAwait(false);
        var answerThreeId = await SeedAssistantTurnAsync(persistence, conversationId, "a-three", createdAtUtc: 16).ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var capturingRunner = new RegenContextCapturingRunner(dispatcher);
        var service = CreateService(persistence, dispatcher, capturingRunner);

        // Regenerate the FIRST answer: the sibling is minted with the next free sequence, i.e. past every later turn.
        ChatStreamEvent? completed = null;
        await foreach (var streamEvent in service.RegenerateAsync(conversationId, answerOneId).ConfigureAwait(false))
        {
            if (streamEvent.Type == ChatStreamEventTypes.AssistantCompleted)
            {
                completed = streamEvent;
            }
        }

        AssertEx.True(completed is not null, "Expected the first regenerate to complete.");
        var lateSiblingId = completed!.MessageId;

        var seeded = AssertEx.NotNull(await persistence.GetConversationAsync(conversationId).ConfigureAwait(false));
        var lateSibling = seeded.Messages.Single(message => message.MessageId == lateSiblingId);
        var lastAnswerSequence = seeded.Messages.Single(message => message.MessageId == answerThreeId).Sequence;
        AssertEx.True(lateSibling.Sequence > lastAnswerSequence,
            "The regenerated sibling must take a physical sequence PAST the later turns — that is the trap under test.");
        AssertEx.True(lateSibling.VariantGroupId is not null, "A regenerated answer must join its original's variant group.");

        // Select the new sibling as the group's active revision, exactly as the UI does after a regenerate.
        await persistence.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest(conversationId,
                             new Dictionary<Guid, Guid>
                             {
                                 [lateSibling.VariantGroupId!.Value] = lateSiblingId
                             },
                             UpdatedAtUtc: 20))
                         .ConfigureAwait(false);

        var drained = 0;
        await foreach (var _ in service.RegenerateAsync(conversationId, answerThreeId).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the second regenerate to stream events.");

        var context = AssertEx.NotNull(capturingRunner.LastContext);
        AssertEx.Equal(expected: 5, context.Count);
        AssertEx.Equal(userOneId, context[0].Id);
        AssertEx.Equal(lateSiblingId, context[1].Id, "The selected late sibling must sit at its group's EARLY position, right after the question it answers.");
        AssertEx.Equal(userTwoId, context[2].Id);
        AssertEx.Equal(answerTwoId, context[3].Id);
        AssertEx.Equal(userThreeId, context[4].Id);
        AssertEx.True(context.All(message => message.Id != answerOneId && message.Id != answerThreeId),
            "Neither the deselected sibling nor the answer being replaced may appear.");
    }

    private static async Task<Guid> SeedUserTurnAsync(NodeChatPersistenceService persistence, Guid conversationId, string content, long createdAtUtc)
    {
        var messageId = Guid.NewGuid();
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversationId, messageId, content, createdAtUtc)).ConfigureAwait(false);
        return messageId;
    }

    private static async Task<Guid> SeedAssistantTurnAsync(NodeChatPersistenceService persistence, Guid conversationId, string content, long createdAtUtc)
    {
        var messageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversationId, messageId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversationId, messageId, correlation.RequestId, createdAtUtc, "model-x"))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation, NodeChatMessageStatusValues.Completed, createdAtUtc, content, Model: "model-x"))
                         .ConfigureAwait(false);
        return messageId;
    }

    [Test]
    public async Task RegenerateAsync_ThreadsSamplingOptionsIntoRuntimePackage()
    {
        // Parity with the send path: the developer-gated per-turn sampling overrides were dropped on the floor by the
        // regenerate hub path, so a rerun silently ignored the temperature/seed the original send used.
        await using var provider = await BuildProviderAsync("regeneration-sampling.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
        var (conversationId, originalId) = await SeedRegeneratableTurnAsync(persistence).ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var capturingRunner = new RegenContextCapturingRunner(dispatcher);
        var service = CreateService(persistence, dispatcher, capturingRunner);

        var sampling = new SamplingOptions
        {
            Temperature = 0.25f,
            Seed = "1234"
        };

        var drained = 0;
        await foreach (var _ in service.RegenerateAsync(conversationId, originalId, samplingOptions: sampling).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the regenerate to stream events.");
        var carried = AssertEx.NotNull(capturingRunner.LastSamplingOptions);
        AssertEx.Equal(expected: 0.25f, carried.Temperature);
        AssertEx.Equal("1234", carried.Seed);
    }

    [Test]
    public async Task RegenerateAsync_WhenNoSamplingOptionsSupplied_LeavesRuntimePackageSamplingNull()
    {
        // The no-override path must stay byte-identical to before the threading landed (the package's config hash
        // feeds runtime reuse).
        await using var provider = await BuildProviderAsync("regeneration-sampling-default.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
        var (conversationId, originalId) = await SeedRegeneratableTurnAsync(persistence).ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var capturingRunner = new RegenContextCapturingRunner(dispatcher);
        var service = CreateService(persistence, dispatcher, capturingRunner);

        var drained = 0;
        await foreach (var _ in service.RegenerateAsync(conversationId, originalId).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the regenerate to stream events.");
        AssertEx.True(capturingRunner.LastSamplingOptions is null, "A regenerate with no overrides must carry no sampling block.");
    }

    [Test]
    public async Task RegenerateAsync_WhenTheConversationIsCompacted_SendsTheSynopsisInPlaceOfTheCoveredHistory()
    {
        // The regenerate path ignored the conversation's compaction synopsis entirely and re-sent every covered
        // message verbatim — the exact context the synopsis exists to replace, and a context-window blow-up on a
        // conversation that was compacted precisely because it no longer fit.
        await using var provider = await BuildProviderAsync("regeneration-compaction.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        var oldUserId = Guid.NewGuid();
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, oldUserId, "ancient question", CreatedAtUtc: 11)).ConfigureAwait(false);
        var oldAssistantId = Guid.NewGuid();
        var oldCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, oldAssistantId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, oldAssistantId, oldCorrelation.RequestId, CreatedAtUtc: 12,
                             "model-x"))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(oldCorrelation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 13, "ancient answer",
                             Model: "model-x"))
                         .ConfigureAwait(false);

        var recentUserId = Guid.NewGuid();
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, recentUserId, "what is 2+2?", CreatedAtUtc: 14)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, CreatedAtUtc: 15,
                             "model-x"))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(
                             new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 16, "four", Model: "model-x"))
                         .ConfigureAwait(false);

        // Compact everything up to (and including) the older assistant answer — the sequence the persistence layer
        // actually assigned it, so the test does not hard-code the numbering.
        var seeded = AssertEx.NotNull(await persistence.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var coveredSequence = seeded.Messages.Single(message => message.MessageId == oldAssistantId).Sequence;
        await persistence.SetCompactionSummaryAsync(new NodeChatSetCompactionSummaryRequest(conversation.ConversationId, "ancient synopsis", coveredSequence, UpdatedAtUtc: 17))
                         .ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var capturingRunner = new RegenContextCapturingRunner(dispatcher);
        var service = CreateService(persistence, dispatcher, capturingRunner);

        var drained = 0;
        await foreach (var _ in service.RegenerateAsync(conversation.ConversationId, originalId).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the regenerate to stream events.");

        // ONE synthetic synopsis message stands in for the covered turns, followed only by the uncovered user turn
        // the rerun is answering.
        var context = AssertEx.NotNull(capturingRunner.LastContext);
        AssertEx.Equal(expected: 2, context.Count);
        AssertEx.Equal(MessageRole.User, context[0].Role);
        AssertEx.True(context[0].Content.Contains("ancient synopsis", StringComparison.Ordinal), "The synopsis must be sent in place of the covered history.");
        AssertEx.Equal(expected: 0, context[0].SortOrder);
        AssertEx.Equal(recentUserId, context[1].Id);
        AssertEx.True(context.All(message => message.Id != oldUserId && message.Id != oldAssistantId), "Covered messages must not be re-sent verbatim.");
    }

    [Test]
    public async Task RegenerateAsync_WithASelectedPathSwitch_DoesNotSendTheSynopsisTheSwitchCleared()
    {
        // Persisting a request-supplied selection CLEARS the conversation's compaction synopsis (the synopsis was built
        // on the previously-selected path). The conversation DTO used to be read BEFORE that write, so the rerun still
        // spliced the now-deleted synopsis in and dropped the very messages it claimed to cover — the branch switch
        // silently rewrote the model's view of the conversation.
        await using var provider = await BuildProviderAsync("regeneration-compaction-path-switch.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        var oldUserId = Guid.NewGuid();
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, oldUserId, "ancient question", CreatedAtUtc: 11)).ConfigureAwait(false);
        var oldAssistantId = Guid.NewGuid();
        var oldCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, oldAssistantId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, oldAssistantId, oldCorrelation.RequestId, CreatedAtUtc: 12,
                             "model-x"))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(oldCorrelation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 13, "ancient answer",
                             Model: "model-x"))
                         .ConfigureAwait(false);

        var recentUserId = Guid.NewGuid();
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, recentUserId, "what is 2+2?", CreatedAtUtc: 14)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, CreatedAtUtc: 15,
                             "model-x"))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(
                             new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 16, "four", Model: "model-x"))
                         .ConfigureAwait(false);

        var seeded = AssertEx.NotNull(await persistence.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var coveredSequence = seeded.Messages.Single(message => message.MessageId == oldAssistantId).Sequence;
        await persistence.SetCompactionSummaryAsync(new NodeChatSetCompactionSummaryRequest(conversation.ConversationId, "ancient synopsis", coveredSequence, UpdatedAtUtc: 17))
                         .ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var capturingRunner = new RegenContextCapturingRunner(dispatcher);
        var service = CreateService(persistence, dispatcher, capturingRunner);

        var drained = 0;
        // Any request-supplied selection map takes the persist-then-clear branch; this map selects no variant itself.
        await foreach (var _ in service.RegenerateAsync(conversation.ConversationId, originalId, selectedPath: new Dictionary<Guid, Guid>()).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the regenerate to stream events.");

        var context = AssertEx.NotNull(capturingRunner.LastContext);
        AssertEx.False(context.Any(message => message.Content.Contains("ancient synopsis", StringComparison.Ordinal)),
            "The path switch cleared the synopsis, so the rerun must not send it.");
        AssertEx.True(context.Any(message => message.Id == oldUserId), "The history the cleared synopsis covered must be sent verbatim again.");
        AssertEx.True(context.Any(message => message.Id == recentUserId), "The answered user turn must still be sent.");
    }

    [Test]
    public async Task RegenerateAsync_WhenTheSynopsisCoversTheAnsweredUserTurn_KeepsTheVerbatimHistory()
    {
        // Guard on the splice: a synopsis that reaches the very user turn the rerun is answering would leave the model
        // with a summary and no question, so that case keeps the pre-cutoff history verbatim and sends no synopsis.
        await using var provider = await BuildProviderAsync("regeneration-compaction-covers-cutoff.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

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

        var seeded = AssertEx.NotNull(await persistence.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var userSequence = seeded.Messages.Single(message => message.MessageId == userMessageId).Sequence;
        await persistence.SetCompactionSummaryAsync(new NodeChatSetCompactionSummaryRequest(conversation.ConversationId, "covers everything", userSequence, UpdatedAtUtc: 14))
                         .ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var capturingRunner = new RegenContextCapturingRunner(dispatcher);
        var service = CreateService(persistence, dispatcher, capturingRunner);

        var drained = 0;
        await foreach (var _ in service.RegenerateAsync(conversation.ConversationId, originalId).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the regenerate to stream events.");
        var context = AssertEx.NotNull(capturingRunner.LastContext);
        AssertEx.Equal(expected: 1, context.Count);
        AssertEx.Equal(userMessageId, context[0].Id);
        AssertEx.False(context.Any(message => message.Content.Contains("covers everything", StringComparison.Ordinal)),
            "A synopsis reaching the answered user turn must not replace it.");
    }

    [Test]
    public async Task RegenerateAsync_ThreadsReasoningEffortIntoRuntimePackage()
    {
        await using var provider = await BuildProviderAsync("regeneration-reasoning.sqlite").ConfigureAwait(false);
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
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
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
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
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
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
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
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
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
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
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
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
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
    public async Task RegenerateAsync_WhenTheConversationIsUnknown_ThrowsTheTypedNotFound()
    {
        // The type is the contract: LocalChatHub matches on it to forward a legible sentence, where a bare
        // InvalidOperationException reaches the browser as SignalR's generic "An unexpected error occurred".
        await using var provider = await BuildProviderAsync("regeneration-missing-conversation.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
        var service = CreateMinimalRegenerationService(persistence);

        _ = await AssertEx.ThrowsAsync<NodeChatConversationNotFoundException>(async () =>
        {
            await foreach (var _ in service.RegenerateAsync(Guid.NewGuid(), Guid.NewGuid()).ConfigureAwait(false))
            {
                // Nothing is ever yielded.
            }
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task RegenerateAsync_WhenTheOriginalMessageIsUnknown_ThrowsTheTypedNotFound()
    {
        await using var provider = await BuildProviderAsync("regeneration-missing-message.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        var service = CreateMinimalRegenerationService(persistence);

        _ = await AssertEx.ThrowsAsync<NodeChatMessageNotFoundException>(async () =>
        {
            await foreach (var _ in service.RegenerateAsync(conversation.ConversationId, Guid.NewGuid()).ConfigureAwait(false))
            {
                // Nothing is ever yielded.
            }
        }).ConfigureAwait(false);
    }

    // The regenerate rejects before any runner/dispatcher work, so the collaborators past persistence never run.
    private static NodeChatRegenerationService CreateMinimalRegenerationService(NodeChatPersistenceService persistence)
    {
        var dispatcher = new RegenRecordingDispatcher();
        return new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
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
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            NullLogger<NodeChatRegenerationService>.Instance);
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
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(classification: classificationService),
                NullLogger<ChatTurnResolver>.Instance),
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
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            NullLogger<NodeChatRegenerationService>.Instance);

        var drained = 0;
        await foreach (var _ in service.RegenerateAsync(conversation.ConversationId, originalId, useLocalTools: true).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the regenerate to stream events.");

        // The Ollama classifier is never consulted for a Codex model id — capabilities come from the Codex matrix.
        await classificationService.DidNotReceive()
                                   .ClassifyAsync(Arg.Is<IEnumerable<ModelIdentity>>(models => models.Any(m => string.Equals(m.ModelName, CodexModel, StringComparison.OrdinalIgnoreCase))),
                                       Arg.Any<CancellationToken>())
                                   .ConfigureAwait(false);

        // Tool calling is enabled for all Codex ids (V0=true), so the requested local tool offer is honored on
        // regenerate. It is requested with isCloudModel: true so the knowledge-tool provider-locality gate applies.
        _ = offerProvider.Received().GetOfferedToolsAsync(CodexModel, true, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegenerateAsync_WhenOnlyClientTokenCancelledMidStream_RunReachesTerminalAndVariantCompletes()
    {
        // A SignalR disconnect cancels ONLY the client cancellationToken (the SSE forward loop), never the
        // run/pump. The run must keep going on the unlinked runCancellation and the variant must persist Completed —
        // never Cancelled/Interrupted from the client connection dropping. This is the send-path shape the regenerate
        // path previously lacked (it linked the run CTS to the client token and cancelled it unconditionally).
        await using var provider = await BuildProviderAsync("regeneration-client-cancel.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
        var (conversation, originalId) = await SeedCompletedOriginalAsync(persistence).ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var runner = new RegenGatedCompletingRunner(dispatcher);
        var service = CreateService(persistence, runner, dispatcher, new NodeChatStreamCancellationRegistry());

        using var clientCts = new CancellationTokenSource();
        var newVariantId = Guid.Empty;
        var consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var streamEvent in service.RegenerateAsync(conversation.ConversationId, originalId, cancellationToken: clientCts.Token).ConfigureAwait(false))
                {
                    if (streamEvent.Type == ChatStreamEventTypes.AssistantPending)
                    {
                        newVariantId = streamEvent.MessageId;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected: the client token cancelled the SSE forward loop. The run/pump keep going underneath.
            }
        });

        await runner.Started.ConfigureAwait(false);
        // The client "disconnects" mid-stream, then the run is allowed to finish.
        await clientCts.CancelAsync().ConfigureAwait(false);
        runner.Release();
        await consumer.ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await persistence.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var variant = loaded.Messages.Single(message => message.MessageId == newVariantId);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, variant.Status);
        AssertEx.Equal("gated answer done", variant.Content);
    }

    [Test]
    public async Task RegenerateAsync_WhenTornDownBeforeOwnership_TerminalizesVariantInterrupted()
    {
        // A disconnect BEFORE run ownership is established (the enumerator is disposed after the Pending
        // frame but before the pump/runner exist) must terminalize the variant Interrupted via the shared
        // PreOwnershipTerminalizationGuard — not leave it stranded Pending/Queued until the restart reaper.
        await using var provider = await BuildProviderAsync("regeneration-preownership.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
        var (conversation, originalId) = await SeedCompletedOriginalAsync(persistence).ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var runner = new RegenGatedCompletingRunner(dispatcher);
        var service = CreateService(persistence, runner, dispatcher, new NodeChatStreamCancellationRegistry());

        var enumerator = service.RegenerateAsync(conversation.ConversationId, originalId).GetAsyncEnumerator();
        Guid newVariantId;
        try
        {
            AssertEx.True(await enumerator.MoveNextAsync(), "Expected the first (Pending) frame.");
            AssertEx.Equal(ChatStreamEventTypes.AssistantPending, enumerator.Current.Type);
            newVariantId = enumerator.Current.MessageId;
        }
        finally
        {
            // Dispose while suspended at the Pending yield — before OwnershipEstablished — so the pre-ownership guard runs.
            await enumerator.DisposeAsync();
        }

        var loaded = AssertEx.NotNull(await persistence.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var variant = loaded.Messages.Single(message => message.MessageId == newVariantId);
        AssertEx.Equal(NodeChatMessageStatusValues.Interrupted, variant.Status);
    }

    [Test]
    public async Task RegenerateAsync_WhenUserCancelsThroughRegistry_TerminalizesVariantCancelled()
    {
        // Regression guard: a genuine user Stop (routed through the cancellation registry) must still cancel
        // the run — the fix preserves user-cancel while decoupling the run from the client connection.
        await using var provider = await BuildProviderAsync("regeneration-user-cancel.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
        var (conversation, originalId) = await SeedCompletedOriginalAsync(persistence).ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var runner = new RegenGatedCompletingRunner(dispatcher);
        var registry = new NodeChatStreamCancellationRegistry();
        var service = CreateService(persistence, runner, dispatcher, registry);

        var newVariantId = Guid.Empty;
        var requestId = Guid.Empty;
        var consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var streamEvent in service.RegenerateAsync(conversation.ConversationId, originalId).ConfigureAwait(false))
                {
                    if (streamEvent.Type == ChatStreamEventTypes.AssistantPending)
                    {
                        newVariantId = streamEvent.MessageId;
                        requestId = streamEvent.RequestId;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // A user cancel may surface as an OCE depending on drain timing; the persisted terminal is authoritative.
            }
        });

        await runner.Started.ConfigureAwait(false);
        AssertEx.True(registry.TryCancel(new NodeChatMessageCorrelation(conversation.ConversationId, newVariantId, requestId)), "The active stream must be found and cancelled.");
        await consumer.ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await persistence.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var variant = loaded.Messages.Single(message => message.MessageId == newVariantId);
        AssertEx.Equal(NodeChatMessageStatusValues.Cancelled, variant.Status);
    }

    // Seeds a conversation with one completed user + assistant turn (the "original" to regenerate) carrying an explicit
    // model, so the regenerate resolves that model and does not touch the local-default resolver.
    private static async Task<(NodeChatConversationDto Conversation, Guid OriginalId)> SeedCompletedOriginalAsync(NodeChatPersistenceService persistence)
    {
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
        return (conversation, originalId);
    }

    private static NodeChatRegenerationService CreateService(NodeChatPersistenceService persistence,
        IInvocationRunner runner,
        RegenRecordingDispatcher dispatcher,
        NodeChatStreamCancellationRegistry registry)
    {
        return new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            registry,
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            CreateTurnContextBuilder(),
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            NullLogger<NodeChatRegenerationService>.Instance);
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
        provider.GetOfferedToolsAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(tools);
        provider.GetOfferedToolsForProfileAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(tools);
        return provider;
    }

    // The default scope factory: no IKnowledgeSearchService wired, so a regenerate that never touches the KB grounding
    // path (the pre-existing tests) resolves nothing. Mirrors the send-path test harness (NodeChatStreamServiceTests).
    private static IServiceScopeFactory CreateScopeFactory(IKnowledgeSearchService? searchService = null)
    {
        var factory = Substitute.For<IServiceScopeFactory>();
        if (searchService is null)
        {
            return factory;
        }

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IKnowledgeSearchService)).Returns(searchService);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        factory.CreateScope().Returns(scope);
        return factory;
    }

    // The turn-context builder the regenerate shares with the send path. A regenerate reaches only its knowledge-base
    // grounding (attachments are a send-only concern), so the attachment collaborators are bare substitutes and the
    // scope factory is what decides whether grounding finds anything.
    private static IChatTurnContextBuilder CreateTurnContextBuilder(IServiceScopeFactory? scopeFactory = null)
    {
        return new ChatTurnContextBuilder(Substitute.For<IConversationUploadedFileStore>(),
            Substitute.For<IUntrustedContentFenceSeedProvider>(),
            scopeFactory ?? CreateScopeFactory(),
            Options.Create(new LocalChatAgentOptions()),
            NullLogger<ChatTurnContextBuilder>.Instance);
    }

    // A knowledge search service returning a fixed hit list, wired into a scope factory for the KB grounding tests.
    private static IServiceScopeFactory CreateKnowledgeScopeFactory(params KnowledgeSearchHit[] hits)
    {
        var searchService = Substitute.For<IKnowledgeSearchService>();
        searchService.SearchAsync(Arg.Any<KnowledgeSearchRequest>(), Arg.Any<CancellationToken>())
                     .Returns(new KnowledgeSearchResult(hits));
        return CreateScopeFactory(searchService);
    }

    private static KnowledgeSearchHit KnowledgeHit(string title, string content, double score)
    {
        return new KnowledgeSearchHit(Guid.NewGuid(), Guid.NewGuid(), title, "Section", content, "knowledge-base", score, ChunkIndex: 0, KnowledgeDocumentStatus.Indexed, ServingLastKnownGood: false);
    }

    // Builds a regeneration service wired with the given scope factory (KB retrieval path) and cloud-egress opt-in,
    // mirroring the inline construction the other regen tests use. cloud-vs-local is chosen by the original turn's model.
    private static NodeChatRegenerationService CreateServiceWithScopeFactory(NodeChatPersistenceService persistence,
        IInvocationRunner runner,
        RegenRecordingDispatcher dispatcher,
        IServiceScopeFactory scopeFactory,
        bool allowCloudModelAccess = false)
    {
        return new NodeChatRegenerationService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
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
            CreateTurnContextBuilder(scopeFactory),
            Options.Create(new KnowledgeBaseOptions
            {
                AllowCloudModelAccess = allowCloudModelAccess
            }),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            NullLogger<NodeChatRegenerationService>.Instance);
    }

    // The default node-settings store: no operator-selected node default, so model resolution falls through to the
    // original turn's model (or the static config fallback) exactly as the send path does.
    private static INodeSettingsStore CreateNodeSettingsStore(string? defaultModelName = null,
        int maxMessageRequestTimeoutSeconds = StoredNodeSettings.DefaultMaxMessageRequestTimeoutSeconds)
    {
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings
        {
            DefaultModelName = defaultModelName,
            MaxMessageRequestTimeoutSeconds = maxMessageRequestTimeoutSeconds
        });
        return store;
    }

    // The default classification service: every model resolves to BOTH thinking- and tools-capable, so the existing
    // think/tool-offer assertions stay byte-identical (these tests pre-date per-model capability gating).
    private static IModelClassificationService CreateModelClassificationService()
    {
        var service = Substitute.For<IModelClassificationService>();
        service.ClassifyAsync(Arg.Any<IEnumerable<ModelIdentity>>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var models = callInfo.Arg<IEnumerable<ModelIdentity>>();
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

    // ChatTurnResolver resolves capabilities through the shared IModelCapabilityResolver, so these tests compose the
    // real one over the same substituted collaborators they used to hand the turn resolver directly — the routing
    // decision under test is unchanged, it just lives in one place now.
    private static IModelCapabilityResolver CreateModelCapabilityResolver(IModelClassificationService? classification = null,
        ILocalModelProviderResolver? providerResolver = null,
        IGgufModelCapabilityResolver? gguf = null)
    {
        return new ModelCapabilityResolver(classification ?? CreateModelClassificationService(),
            providerResolver ?? CreateLocalModelProviderResolver(),
            gguf ?? CreateGgufModelCapabilityResolver(),
            Substitute.For<IActiveCloudChatClientFactory>(),
            new FakeModelTrustResolver(),
            NullLogger<ModelCapabilityResolver>.Instance);
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
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns((ResolvedAgentRuntime?)null);
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
        resolver.ResolveAsync(Arg.Any<AgentDefinitionRecord>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(OrchestrationResolution.NotOrchestrated);
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

    // beforeStreaming lets a test raise dispatcher traffic (an approval, a notice) from INSIDE the run, which is the
    // only point at which the service's handlers are subscribed. Optional, so every existing construction is unchanged.
    private sealed class RegenCompletingRunner(RegenRecordingDispatcher dispatcher, Func<Guid, Task>? beforeStreaming = null) : IInvocationRunner
    {
        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            if (beforeStreaming is not null)
            {
                await beforeStreaming(context.Package.InvocationId).ConfigureAwait(false);
            }

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

        public void CancelDetached(Guid invocationId)
        {
        }

        public void CancelAll()
        {
        }

        public void CleanupStaleToolCalls(TimeSpan maxAge)
        {
        }

        public void ResolveApprovalResult(ApprovalResolvedEvent evt, ApprovalScope scope = ApprovalScope.Once)
        {
        }

        public void ResolveUserQuestionResult(UserQuestionAnsweredEvent evt)
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

        // The timeout block carried on the runtime package; the node-settings test asserts the operator's
        // "Maximum message request timeout" reaches a regenerated turn too, not just the send path.
        public TimeoutSettings? LastTimeouts { get; private set; }

        // The per-turn sampling overrides carried on the runtime package; the sampling tests assert the developer-mode
        // knobs reach a regenerated turn too, and that an override-free rerun still carries none.
        public SamplingOptions? LastSamplingOptions { get; private set; }
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
            LastTimeouts = context.Package.Timeouts;
            LastSamplingOptions = context.Package.SamplingOptions;
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

        public void CancelDetached(Guid invocationId)
        {
        }

        public void CancelAll()
        {
        }

        public void CleanupStaleToolCalls(TimeSpan maxAge)
        {
        }

        public void ResolveApprovalResult(ApprovalResolvedEvent evt, ApprovalScope scope = ApprovalScope.Once)
        {
        }

        public void ResolveUserQuestionResult(UserQuestionAnsweredEvent evt)
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

        public void CancelDetached(Guid invocationId)
        {
        }

        public void CancelAll()
        {
        }

        public void CleanupStaleToolCalls(TimeSpan maxAge)
        {
        }

        public void ResolveApprovalResult(ApprovalResolvedEvent evt, ApprovalScope scope = ApprovalScope.Once)
        {
        }

        public void ResolveUserQuestionResult(UserQuestionAnsweredEvent evt)
        {
        }

        public void ResolveToolCallResult(ToolCallResultEvent evt)
        {
        }
    }

    // Streams one chunk, signals Started, then blocks until Release() (or the run token cancels — honoring a genuine
    // user cancel), then finishes Completed. Lets a test interleave a client-token cancel / teardown / user cancel with
    // an in-flight run.
    private sealed class RegenGatedCompletingRunner(RegenRecordingDispatcher dispatcher) : IInvocationRunner
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public int ActiveInvocationCount => 0;

        public void Release()
        {
            _release.TrySetResult();
        }

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            await dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, "gated answer").ConfigureAwait(false);
            _started.TrySetResult();

            // Honor a genuine user cancel (runCancellation) while blocked; a client-token disconnect never reaches here.
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            await dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, " done").ConfigureAwait(false);
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

        public void CancelDetached(Guid invocationId)
        {
        }

        public void CancelAll()
        {
        }

        public void CleanupStaleToolCalls(TimeSpan maxAge)
        {
        }

        public void ResolveApprovalResult(ApprovalResolvedEvent evt, ApprovalScope scope = ApprovalScope.Once)
        {
        }

        public void ResolveUserQuestionResult(UserQuestionAnsweredEvent evt)
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

        public event EventHandler<ApprovalRequestedChangedEventArgs>? ApprovalRequestedChanged;

        public event EventHandler<UserQuestionRequestedChangedEventArgs>? UserQuestionRequestedChanged;

        // The dispatcher is a SINGLETON in the app, so a handler left attached leaks for the process lifetime and
        // keeps firing into a stream nobody reads. Tests assert this is false on every exit path.
        public bool HasApprovalSubscribers => ApprovalRequestedChanged is not null;

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

        public Task DispatchApprovalResolvedAsync(ApprovalResolvedEvent evt, ApprovalScope scope = ApprovalScope.Once)
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

        public Task ReportInvocationPhaseAsync(Guid invocationId, InvocationRuntimePhase phase)
        {
            if (CurrentInvocation is not null)
            {
                CurrentInvocation.RuntimePhase = phase;
            }

            return Task.CompletedTask;
        }

        public Task ReportInvocationCompletedAsync(Guid invocationId, int? inputTokens = null, int? outputTokens = null, int? totalTokens = null, int? reasoningTokens = null,
            long? generationDurationMs = null, string? finishReason = null, InvocationThroughput? throughput = null)
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

        public Task ReportToolSchemaTokensAsync(Guid invocationId, long? toolSchemaTokens, int? maxToolSchemaTokens)
        {
            if (CurrentInvocation is not null)
            {
                CurrentInvocation.ToolSchemaTokens = toolSchemaTokens;
                CurrentInvocation.MaxToolSchemaTokens = maxToolSchemaTokens;
            }

            return Task.CompletedTask;
        }

        public Task ReportTurnTelemetryAsync(Guid invocationId, long? modelReadinessMs, TurnUsageTotals? usage)
        {
            if (CurrentInvocation is not null)
            {
                CurrentInvocation.ModelReadinessMs = modelReadinessMs;
                CurrentInvocation.TurnInputTokens = usage?.InputTokens;
                CurrentInvocation.TurnOutputTokens = usage?.OutputTokens;
                CurrentInvocation.TurnTotalTokens = usage?.TotalTokens;
                CurrentInvocation.TurnReasoningTokens = usage?.ReasoningTokens;
            }

            return Task.CompletedTask;
        }

        public Task ReportEffortDispatchAsync(Guid invocationId, string dispatchedTier, string authoredEffort)
        {
            if (CurrentInvocation is not null)
            {
                CurrentInvocation.DispatchedTier = dispatchedTier;
                CurrentInvocation.AuthoredEffort = authoredEffort;
            }

            return Task.CompletedTask;
        }

        public Task ReportServedModelAsync(Guid invocationId, string modelUsed)
        {
            if (CurrentInvocation is not null)
            {
                CurrentInvocation.ModelUsed = modelUsed;
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

        public Task ReportApprovalLifecycleAsync(ApprovalLifecyclePayload payload)
        {
            ApprovalRequestedChanged?.Invoke(this, new ApprovalRequestedChangedEventArgs(payload));
            return Task.CompletedTask;
        }

        public Task ReportUserQuestionAsync(UserQuestionLifecyclePayload payload)
        {
            UserQuestionRequestedChanged?.Invoke(this, new UserQuestionRequestedChangedEventArgs(payload));
            return Task.CompletedTask;
        }

        public Task DispatchUserQuestionAnsweredAsync(UserQuestionAnsweredEvent evt)
        {
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
