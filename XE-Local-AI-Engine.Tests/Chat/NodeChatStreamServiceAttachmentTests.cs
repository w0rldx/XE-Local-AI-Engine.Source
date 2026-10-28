namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using static XE_Local_AI_Engine.Tests.Chat.NodeChatStreamServiceTestSupport;

[Category(TestCategories.Unit)]
public sealed class NodeChatStreamServiceAttachmentTests
{
    /// <summary>A file of another conversation is refused by name before the user turn is persisted, never dropped silently.</summary>
    [Test]
    public async Task SendMessageAsync_WhenAnAttachmentBelongsToAnotherConversation_RefusesNamingIt()
    {
        var conversationId = Guid.NewGuid();
        var ownFileId = Guid.NewGuid();
        var foreignFileId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, Guid.NewGuid(), Guid.NewGuid(), _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ContextCapturingInvocationRunner(dispatcher);
        var uploadedFileStore = Substitute.For<IConversationUploadedFileStore>();
        uploadedFileStore.ListAsync(conversationId, Arg.Any<CancellationToken>())
                         .Returns([UploadedFile(conversationId, ownFileId)]);
        var service = new NodeChatStreamService(persistence,
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
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(uploadedFileStore),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());

        var streamed = 0;
        var exception = await AssertEx.ThrowsAsync<NodeChatInvalidRequestException>(async () =>
        {
            await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                               "summarize the attachment",
                               AttachmentFileIds: [ownFileId, foreignFileId])))
            {
                streamed++;
            }
        });

        AssertEx.Contains(exception.Message, foreignFileId.ToString());
        AssertEx.False(exception.Message.Contains(ownFileId.ToString(), StringComparison.Ordinal), exception.Message);
        AssertEx.Equal(expected: 0, streamed);
        AssertEx.False(runner.CaptureObserved, "A refused send must never reach the runner.");
        await persistence.DidNotReceiveWithAnyArgs().PersistUserMessageAsync(default!, default);
    }

    [Test]
    public async Task SendMessageAsync_WhenPlainChatWithAttachment_InjectsExtractedTextIntoContext()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ContextCapturingInvocationRunner(dispatcher);

        IReadOnlyList<ConversationUploadedFileInfo> files =
        [
            new ConversationUploadedFileInfo
            {
                FileId = fileId,
                ConversationId = conversationId,
                OriginalFileName = "spec.txt",
                MimeType = "text/plain",
                Extension = ".txt",
                SizeBytes = 128,
                ExtractionStatus = DocumentExtractionStatus.Extracted,
                ExtractedChars = 24,
                CreatedAtUtc = 0
            }
        ];
        var uploadedFileStore = Substitute.For<IConversationUploadedFileStore>();
        uploadedFileStore.ListAsync(conversationId, Arg.Any<CancellationToken>()).Returns(files);
        uploadedFileStore.ReadExtractedMarkdownAsync(conversationId, fileId, Arg.Any<CancellationToken>()).Returns("The launch code is alpha-zero.");

        var service = new NodeChatStreamService(persistence,
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
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(uploadedFileStore),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "what is the launch code?",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           AttachmentFileIds: [fileId])))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.True(runner.CaptureObserved, "Expected the runner to observe the package.");
        AssertEx.Contains(runner.CapturedContext, message => message.Content.Contains("The launch code is alpha-zero.", StringComparison.Ordinal));
        AssertEx.Contains(runner.CapturedContext, message => message.Content.Contains(ConversationAttachmentContextComposer.Preamble, StringComparison.Ordinal));
    }

    [Test]
    public async Task SendMessageAsync_WhenModelIsVisionCapable_AttachesImagePartsToContext()
    {
        var captured = await RunImageAttachmentAsync(supportsVision: true);

        var imageMessage = captured.SingleOrDefault(message => message.Images is { Count: > 0 });
        AssertEx.NotNull(imageMessage);
        AssertEx.Equal(expected: 1, imageMessage!.Images!.Count);
        AssertEx.Equal("image/png", imageMessage.Images[0].MediaType);
        AssertEx.True(imageMessage.Images[0].Data.Span.SequenceEqual(new byte[]
            {
                1,
                2,
                3,
                4,
                5
            }),
            "The decrypted image bytes must ride the synthetic context message.");
    }

    [Test]
    public async Task SendMessageAsync_WhenModelIsNotVisionCapable_OmitsImageParts()
    {
        var captured = await RunImageAttachmentAsync(supportsVision: false);

        AssertEx.False(captured.Any(message => message.Images is { Count: > 0 }),
            "A non-vision model must never receive image parts.");
    }

    [Test]
    public async Task SendMessageAsync_WhenLocalToolsEnabledOnVisionModel_StillAttachesImageParts()
    {
        // The tool/agent branch stages only TEXT for the file tools; images must still reach a vision model, so image
        // parts are composed independently of that branch (regression: they were dropped when tools were enabled).
        var captured = await RunImageAttachmentAsync(supportsVision: true, supportsTools: true, useLocalTools: true);

        var imageMessage = captured.SingleOrDefault(message => message.Images is { Count: > 0 });
        AssertEx.NotNull(imageMessage);
        AssertEx.Equal(expected: 1, imageMessage!.Images!.Count);
    }

    [Test]
    public async Task SendMessageAsync_WhenImagesExceedTheCap_AttachesOnlyUpToTheLimit()
    {
        // The client re-sends every conversation attachment each turn; the per-turn image count cap bounds how many are
        // decrypted and attached so a large conversation cannot exhaust the node.
        var options = new LocalChatAgentOptions
        {
            MaxImageAttachments = 3
        };
        var captured = await RunImageAttachmentAsync(supportsVision: true, imageCount: 10, options: options);

        var imageMessage = captured.SingleOrDefault(message => message.Images is { Count: > 0 });
        AssertEx.NotNull(imageMessage);
        AssertEx.Equal(expected: 3, imageMessage!.Images!.Count);
    }

    [Test]
    public async Task SendMessageAsync_WhenCloudEffectiveModelWithKnowledgeBase_WithholdsAndNotifiesByDefault()
    {
        // The KB egress gate mirrors attachments: a cloud effective model must NOT receive KB context without the
        // operator opt-in. The turn runs without KB context and the user gets a KnowledgeWithheld notice.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ContextCapturingInvocationRunner(dispatcher);
        var scopeFactory = CreateKnowledgeScopeFactory(KnowledgeHit("Runbook", "secret runbook body", score: 0.9));

        var service = CreateServiceWithScopeFactory(persistence, runner, dispatcher, scopeFactory, allowCloudModelAccess: false);

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "how do I restart the service?",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           Model: "gpt-5.6-sol",
                           UseKnowledgeBase: true)))
        {
            events.Add(streamEvent);
        }

        AssertEx.False(runner.CapturedContext.Any(message => message.Content.Contains(KnowledgeChatContextComposer.Preamble, StringComparison.Ordinal)),
            "the KB context block must not be composed for a cloud model without opt-in.");
        AssertEx.False(runner.CapturedContext.Any(message => message.Content.Contains("secret runbook body", StringComparison.Ordinal)),
            "a cloud model must not receive KB content without opt-in.");
        AssertEx.Contains(events, streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantNotice
                                                 && streamEvent.NoticeKind == nameof(TurnNoticeKind.KnowledgeWithheld));
        AssertEx.Contains(events.First(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.KnowledgeWithheld)).NoticeMessage,
            "grant local data on the model's external connection");
    }

    [Test]
    public async Task SendMessageAsync_WhenCloudEffectiveModelWithAttachment_WithholdsAndNotifiesByDefault()
    {
        // A cloud effective model (Codex here) must NOT receive node-local attachment content without the
        // operator opt-in — the content is withheld from the prompt and the user gets a visible notice.
        var (events, capturedContext) = await RunAttachmentEgressAsync(cloudModel: "gpt-5.6-sol", allowCloudModelAccess: false);

        AssertEx.False(capturedContext.Any(message => message.Content.Contains("The launch code is alpha-zero.", StringComparison.Ordinal)),
            "a cloud model must not receive the attachment content without opt-in");
        AssertEx.False(capturedContext.Any(message => message.Content.Contains(ConversationAttachmentContextComposer.Preamble, StringComparison.Ordinal)),
            "the attachment context block must not be composed for a cloud model without opt-in");
        AssertEx.Contains(events, streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantNotice
                                                 && streamEvent.NoticeKind == nameof(TurnNoticeKind.AttachmentsWithheld));
        AssertEx.Contains(events.First(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.AttachmentsWithheld)).NoticeMessage,
            "grant local data on the model's external connection");
    }

    [Test]
    public async Task SendMessageAsync_WhenCloudEffectiveModelWithAttachment_AndOperatorOptedIn_ComposesAttachment()
    {
        var (_, capturedContext) = await RunAttachmentEgressAsync(cloudModel: "gpt-5.6-sol", allowCloudModelAccess: true);

        AssertEx.Contains(capturedContext, message => message.Content.Contains("The launch code is alpha-zero.", StringComparison.Ordinal));
    }

    [Test]
    public async Task SendMessageAsync_WhenTheCloudModelsConnectionGrantsLocalData_ComposesAttachmentWithTheSwitchOff()
    {
        const string gatewayModel = "ext:gateway/qwen3";
        var trustResolver = new FakeModelTrustResolver().Register("gateway",
            "qwen3",
            ExternalProviderLocality.Cloud,
            cloudGrants: new ExternalProviderCloudGrants
            {
                LocalData = true
            });

        var (events, capturedContext) = await RunAttachmentEgressAsync(cloudModel: gatewayModel, allowCloudModelAccess: false, trustResolver: trustResolver);

        AssertEx.Contains(capturedContext, message => message.Content.Contains("The launch code is alpha-zero.", StringComparison.Ordinal));
        AssertEx.False(events.Any(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.AttachmentsWithheld)),
            "the connection's grant stands in for AllowCloudModelAccess, so nothing was withheld");
    }

    [Test]
    public async Task SendMessageAsync_WhenALocalTurnRunsAnAgentPinnedToAGrantedCloudModel_ComposesAttachmentWithTheSwitchOff()
    {
        // The active model is the local default; the bound agent's pin is the EFFECTIVE model, and its grants must decide.
        const string pinnedModel = "ext:gateway/qwen3";
        var trustResolver = new FakeModelTrustResolver().Register("gateway",
            "qwen3",
            ExternalProviderLocality.Cloud,
            cloudGrants: new ExternalProviderCloudGrants
            {
                LocalData = true
            });
        var agentResolver = Substitute.For<IAgentDefinitionResolver>();
        agentResolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                     .Returns(new ResolvedAgentRuntime("Pinned persona.", [], pinnedModel, null, 9, Guid.NewGuid(), "Pinned Agent", EffectiveModelIsCloud: true));

        var (events, capturedContext) = await RunAttachmentEgressAsync(cloudModel: null, allowCloudModelAccess: false, trustResolver: trustResolver, agentResolver: agentResolver);

        AssertEx.Contains(capturedContext, message => message.Content.Contains("The launch code is alpha-zero.", StringComparison.Ordinal));
        AssertEx.False(events.Any(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.AttachmentsWithheld)),
            "the pinned model's grant decides, not the local active model's absence of one");
    }

    [Test]
    public async Task SendMessageAsync_WhenLocalModelWithAttachment_ComposesAttachmentAndDoesNotNotify()
    {
        var (events, capturedContext) = await RunAttachmentEgressAsync(cloudModel: null, allowCloudModelAccess: false);

        AssertEx.Contains(capturedContext, message => message.Content.Contains("The launch code is alpha-zero.", StringComparison.Ordinal));
        AssertEx.False(events.Any(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.AttachmentsWithheld)),
            "a local model must not trigger the attachments-withheld notice");
        AssertEx.False(events.Any(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.AttachmentsNotSent)),
            "a turn whose every file reached the model must not report one as unsent");
        AssertEx.False(events.Any(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.AttachmentsTruncated)),
            "a file within the attachment budget is not truncated");
    }

    [Test]
    public async Task SendMessageAsync_WhenAPlainChatFileExceedsTheAttachmentBudget_NamesItInATruncatedNotice()
    {
        // The composer cut the text and told only the model; the user saw a complete-looking answer (4.4).
        var (events, _) = await RunAttachmentEgressAsync(cloudModel: null, allowCloudModelAccess: false, extraFiles:
        [
            new ExtraUploadedFile("long.pdf", DocumentExtractionStatus.Extracted, ExtractedChars: StoredNodeSettings.DefaultMaxInlinedAttachmentChars + 1)
        ]);

        var notice = events.Single(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.AttachmentsTruncated));
        AssertEx.Equal("long.pdf", notice.NoticeDetail);
    }

    [Test]
    public async Task SendMessageAsync_WhenALocalTurnSendsFilesTheModelNeverGets_NamesThemInOneNotice()
    {
        // A second file with no extracted text (a scanned PDF) and an image on a model without vision were both skipped with no sign to the user.
        var (events, _) = await RunAttachmentEgressAsync(cloudModel: null, allowCloudModelAccess: false, extraFiles:
        [
            new ExtraUploadedFile("scan.pdf", DocumentExtractionStatus.Extracted, ExtractedChars: 0),
            new ExtraUploadedFile("photo.png", DocumentExtractionStatus.Image, ExtractedChars: null)
        ]);

        var notice = events.Single(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.AttachmentsNotSent));
        AssertEx.Equal("scan.pdf, photo.png", notice.NoticeDetail);
        AssertEx.False(events.Any(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.AttachmentsWithheld)),
            "a local turn's unsent files are not a cloud withhold");
    }

    [Test]
    public async Task SendMessageAsync_WhenATurnOffersToolsButNotTheFileTools_NamesTheAttachmentAsUnsent()
    {
        // Such a turn neither inlines the text nor stages the file for the AgentHome tools, so the text silently vanished.
        var (events, _) = await RunAttachmentEgressAsync(cloudModel: null, allowCloudModelAccess: false, offerToolsWithoutFileTools: true);

        var notice = events.Single(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.AttachmentsNotSent));
        AssertEx.Equal("spec.txt", notice.NoticeDetail);
        AssertEx.Equal("Some attached files were not sent to the model: this turn offers tools but not the file tools, so attachment text is left out, or the model cannot see images.",
            notice.NoticeMessage);
    }

    [Test]
    public async Task SendMessageAsync_WhenAgentModeWithAttachment_DoesNotInlineExtractedText()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ContextCapturingInvocationRunner(dispatcher);
        var offerProvider = CreateOfferProvider(CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}"));
        var uploadedFileStore = Substitute.For<IConversationUploadedFileStore>();
        uploadedFileStore.ListAsync(conversationId, Arg.Any<CancellationToken>())
                         .Returns([UploadedFile(conversationId, fileId)]);

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
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
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(uploadedFileStore),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "summarize the attachment",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           UseLocalTools: true,
                           AttachmentFileIds: [fileId])))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.True(runner.CaptureObserved, "Expected the runner to observe the package.");
        AssertEx.False(runner.CapturedContext.Any(message => message.Content.Contains(ConversationAttachmentContextComposer.Preamble, StringComparison.Ordinal)),
            "Agent mode must not inline attachment text — the agent reads the staged files via its tools.");
        // The unsent-attachment check lists the upload metadata on every allowed turn; the extracted text is never read.
        await uploadedFileStore.DidNotReceive().ReadExtractedMarkdownAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
