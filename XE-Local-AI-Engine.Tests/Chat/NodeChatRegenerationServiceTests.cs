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
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeChatRegenerationServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    [Test]
    public async Task RegenerateAsync_ProducesCompletedSiblingVariantInSameGroupAndStreamsEvents()
    {
        await using var provider = await BuildProviderAsync("regeneration.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        // Seed a completed turn: user question + a completed assistant answer (the "original" to regenerate).
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", 10)).ConfigureAwait(false);
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "what is 2+2?", 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, 12, "model-x")).ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, 13, "four", Model: "model-x")).ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var runner = new RegenCompletingRunner(dispatcher);
        var service = new NodeChatRegenerationService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
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
        AssertEx.Equal(2, variants.Count);
        var groups = variants.Select(v => v.VariantGroupId).Distinct().ToList();
        AssertEx.Equal(1, groups.Count);
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
    public async Task RegenerateAsync_WhenToolLifecycleReported_StreamsToolCallEvents()
    {
        await using var provider = await BuildProviderAsync("regeneration-tool-lifecycle.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        // Seed a completed turn: user question + a completed assistant answer (the "original" to regenerate).
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", 10)).ConfigureAwait(false);
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "what is the weather?", 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, 12, "model-x")).ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, 13, "cloudy", Model: "model-x")).ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var runner = new RegenToolEmittingRunner(dispatcher);
        var service = new NodeChatRegenerationService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
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
        AssertEx.Equal(false, requested.RequiresApproval);
        AssertEx.Equal(NodeChatMessageStatusValues.Streaming, requested.Status);

        var completed = events.Single(streamEvent => streamEvent.Type == ChatStreamEventTypes.ToolCallCompleted);
        AssertEx.Equal("call-1", completed.ToolCallId);
        AssertEx.Equal("weather", completed.ToolName);
        AssertEx.Equal("sunny", completed.Result);
        AssertEx.Equal(false, completed.IsError);

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

        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", 10)).ConfigureAwait(false);
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "what time is it?", 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, 12, "model-x")).ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, 13, "noon", Model: "model-x")).ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var capturingRunner = new RegenContextCapturingRunner(dispatcher);
        var offerProvider = CreateOfferProvider(
            CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}"),
            CreateLocalToolDto("Calculate", "{\"type\":\"object\"}"));
        var service = new NodeChatRegenerationService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            capturingRunner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions { EnableTools = true }),
            new NodeChatStreamCancellationRegistry(),
            offerProvider,
            TimeProvider.System,
            NullLogger<NodeChatRegenerationService>.Instance);

        var drained = 0;
        await foreach (var _ in service.RegenerateAsync(conversation.ConversationId, originalId, useLocalTools: true).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the regenerate to stream events.");
        AssertEx.Equal(2, capturingRunner.LastAllowedTools.Count);
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

        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", 10)).ConfigureAwait(false);
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "what time is it?", 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, 12, "model-x")).ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, 13, "noon", Model: "model-x")).ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var capturingRunner = new RegenContextCapturingRunner(dispatcher);
        var offerProvider = CreateOfferProvider(
            CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}"),
            CreateLocalToolDto("Calculate", "{\"type\":\"object\"}"));
        var service = new NodeChatRegenerationService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            capturingRunner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions { EnableTools = true }),
            new NodeChatStreamCancellationRegistry(),
            offerProvider,
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
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", 10)).ConfigureAwait(false);
        var userMessageId = Guid.NewGuid();
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, userMessageId, "what is 2+2?", 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, 12, "model-x")).ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, 13, "four", Model: "model-x")).ConfigureAwait(false);

        // First regenerate makes variant B (a sibling of the original whose parent is the ORIGINAL assistant turn).
        var dispatcher = new RegenRecordingDispatcher();
        var capturingRunner = new RegenContextCapturingRunner(dispatcher);
        var service = new NodeChatRegenerationService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            capturingRunner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
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
        AssertEx.Equal(1, contextForVariant.Count);
        AssertEx.Equal(userMessageId, contextForVariant[0].Id);
    }

    [Test]
    public async Task RegenerateAsync_ThreadsReasoningEffortIntoRuntimePackage()
    {
        await using var provider = await BuildProviderAsync("regeneration-reasoning.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        // Seed a completed turn: user question + a completed assistant answer (the "original" to regenerate).
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", 10)).ConfigureAwait(false);
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "what is 2+2?", 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, 12, "model-x")).ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, 13, "four", Model: "model-x")).ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var capturingRunner = new RegenContextCapturingRunner(dispatcher);
        var service = new NodeChatRegenerationService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            capturingRunner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
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

        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Regen", "node", 10)).ConfigureAwait(false);
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "what is 2+2?", 11)).ConfigureAwait(false);
        var originalId = Guid.NewGuid();
        var originalCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, originalId, Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalId, originalCorrelation.RequestId, 12, "model-x")).ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(originalCorrelation, NodeChatMessageStatusValues.Completed, 13, "four", Model: "model-x")).ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var capturingRunner = new RegenContextCapturingRunner(dispatcher);
        var service = new NodeChatRegenerationService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            capturingRunner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
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
        await persistence.EnsureConversationAsync(new NodeChatEnsureConversationRequest(conversationId, "Remote", "client-node", 10, NodeChatOriginValues.Remote)).ConfigureAwait(false);

        var dispatcher = new RegenRecordingDispatcher();
        var service = new NodeChatRegenerationService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            new RegenCompletingRunner(dispatcher),
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            TimeProvider.System,
            NullLogger<NodeChatRegenerationService>.Instance);

        await AssertEx.ThrowsAsync<NodeChatReadOnlyConversationException>(async () =>
        {
            var drained = 0;
            await foreach (var _ in service.RegenerateAsync(conversationId, Guid.NewGuid()).ConfigureAwait(false))
            {
                drained++;
            }

            AssertEx.Equal(0, drained);
        }).ConfigureAwait(false);
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
        provider.GetOfferedTools().Returns(tools);
        return provider;
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
            await dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId, 5, 2, 7, 0).ConfigureAwait(false);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

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
        public int ActiveInvocationCount => 0;

        // The conversation context handed to the most recent invocation; the test asserts the variant
        // regenerate never includes a sibling assistant answer.
        public IReadOnlyList<ConversationMessageDto>? LastContext { get; private set; }

        // The reasoning effort carried on the runtime package; the test asserts the regenerate honors the
        // current reasoning selection threaded from the hub.
        public string? LastReasoningEffort { get; private set; }

        // The offer list carried on the runtime package; the test asserts the local tool catalog reaches the
        // runtime package on regenerate only when the client opted in.
        public IReadOnlyList<AllowedToolDto> LastAllowedTools { get; private set; } = [];

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context.Package.ConversationContext;
            LastReasoningEffort = context.Package.ReasoningEffort;
            LastAllowedTools = context.Package.AllowedTools;
            await dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, "regenerated answer").ConfigureAwait(false);
            await dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId, 5, 2, 7, 0).ConfigureAwait(false);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

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
            await dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId, 5, 2, 7, 0).ConfigureAwait(false);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

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

        public InvocationState? CurrentInvocation { get; private set; }

        public bool IsAcceptingRemoteInvocations => true;

        public void StopAcceptingRemoteInvocations()
        {
        }

        public Task DispatchInvocationAssignedAsync(EncryptedRuntimePackageDto package) => Task.CompletedTask;

        public Task DispatchInvocationAssignedV2Async(InvocationAssignedEnvelope envelope) => Task.CompletedTask;

        public Task DispatchToolCallResultAsync(ToolCallResultEvent evt) => Task.CompletedTask;

        public Task DispatchDisconnectRequestedAsync(DisconnectRequestedEvent evt) => Task.CompletedTask;

        public Task DispatchApprovalResolvedAsync(ApprovalResolvedEvent evt) => Task.CompletedTask;

        public Task DispatchInvocationCancelledAsync(InvocationCancelledEvent evt) => Task.CompletedTask;

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

        public Task ReportInvocationCompletedAsync(Guid invocationId, int? inputTokens = null, int? outputTokens = null, int? totalTokens = null, int? reasoningTokens = null)
        {
            if (CurrentInvocation is not null)
            {
                CurrentInvocation.Status = InvocationStatus.Completed;
                CurrentInvocation.CompletedAt = DateTimeOffset.UtcNow;
                CurrentInvocation.InputTokens = inputTokens;
                CurrentInvocation.OutputTokens = outputTokens;
                CurrentInvocation.TotalTokens = totalTokens;
                CurrentInvocation.ReasoningTokens = reasoningTokens;
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

        public Task ReportToolCallRequestedAsync(ToolCallRequestPayload payload) => Task.CompletedTask;

        public Task ReportApprovalRequestedAsync(ApprovalRequestPayload payload) => Task.CompletedTask;

        public Task ReportToolCallLifecycleAsync(ToolCallLifecyclePayload payload)
        {
            ToolCallLifecycleChanged?.Invoke(this, new ToolCallLifecycleChangedEventArgs(payload));
            return Task.CompletedTask;
        }

        private void RaiseChanged()
        {
            InvocationStateChanged?.Invoke(this, new InvocationStateChangedEventArgs(CurrentInvocation!));
        }

        private sealed class NoopLease : IAsyncDisposable
        {
            public static readonly NoopLease Instance = new();

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
