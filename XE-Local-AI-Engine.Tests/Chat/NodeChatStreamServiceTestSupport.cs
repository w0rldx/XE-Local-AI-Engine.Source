namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Coder;
using XE_Local_AI_Engine.Client.Services.Coder.Tools;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.WebAccess;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>Service construction, scripted runners and fakes shared by the NodeChatStreamService test classes.</summary>
internal static partial class NodeChatStreamServiceTestSupport
{
    /// <summary>The model these two tests pin, so the turn and the early check resolve the same head.</summary>
    internal const string WriteDeclarationModel = "tool-capable-model";

    /// <summary>The write/execute tool the widened definition gains — not one of the work-session state tools.</summary>
    internal const string WriteDeclarationToolName = "write_the_repository";

    /// <summary>
    ///     A resolver standing in for a definition an operator widens: narrow on its FIRST answer, write-capable on
    ///     every one after. Two reads of one mutable row are what the race is made of, so the stub answers as that row
    ///     would.
    /// </summary>
    internal static IAgentDefinitionResolver CreateWideningAgentDefinitionResolver(Guid agentDefinitionId)
    {
        var resolved = 0;
        var narrow = new ResolvedAgentRuntime("Persona.", [CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}")], WriteDeclarationModel, null, 1,
            agentDefinitionId);
        var widened = narrow with
        {
            AllowedTools =
            [
                CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}"),
                CreateLocalToolDto(WriteDeclarationToolName, "{\"type\":\"object\"}", ToolCategory.WriteExecute)
            ]
        };

        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<ResolvedAgentRuntime?>(Interlocked.Increment(ref resolved) == 1 ? narrow : widened));
        return resolver;
    }

    internal static NodeChatStreamService CreateWriteDeclarationService(INodeChatPersistenceService persistence,
        ReasoningCapturingInvocationRunner runner,
        RecordingWorkerEventDispatcher dispatcher,
        IAgentDefinitionResolver resolver,
        ILocalToolOfferProvider offerProvider)
    {
        return new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelCapabilityResolver(),
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
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());
    }

    internal static async Task<IAgentHomeExecutionLease> AcquireLeaseAfterYieldAsync(IAgentHomeExecutionLeaseManager leases,
        AgentHomeExecutionLeaseKey key)
    {
        await Task.Yield();
        AssertEx.Succeeded(leases.TryAcquire(key, out var lease));
        return lease;
    }

    internal static IGraphWorkflowStore BoundRunStore(Guid conversationId, GraphWorkflowRunStatus status)
    {
        var store = Substitute.For<IGraphWorkflowStore>();
        store.ListRunsByConversationAsync(conversationId, Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<GraphWorkflowRunSnapshot>>([
                 new GraphWorkflowRunSnapshot
                 {
                     Id = Guid.NewGuid(),
                     RequestId = Guid.NewGuid(),
                     DefinitionId = Guid.NewGuid(),
                     DefinitionVersion = 1,
                     GraphHash = "hash",
                     Status = status,
                     FailureClass = GraphWorkflowFailureClass.None,
                     GraphJson = "{}",
                     InputJson = null,
                     OutputJson = null,
                     Seq = 1,
                     Version = 1,
                     CancelRequestedAtUtc = null,
                     StartedAtUtc = null,
                     CompletedAtUtc = null,
                     CreatedAtUtc = 1
                 }
             ]));
        return store;
    }

    internal static ConversationUploadedFileInfo UploadedFile(Guid conversationId, Guid fileId) =>
        new()
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
        };

    // Runs a plain-chat send whose only attachment is an Image-status file, through a node-local (GGUF-routed) model
    // whose vision capability is toggled by supportsVision, and returns the context the runner observed. A non-Ollama
    // provider name forces the GGUF capability branch (where SupportsVision is resolved); the default Ollama resolver
    // would bypass it and never surface vision.
    internal static async Task<IReadOnlyList<ConversationMessageDto>> RunImageAttachmentAsync(bool supportsVision,
        bool supportsTools = false,
        bool useLocalTools = false,
        int imageCount = 1,
        LocalChatAgentOptions? options = null)
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ContextCapturingInvocationRunner(dispatcher);

        var uploadedFileStore = Substitute.For<IConversationUploadedFileStore>();
        var files = new List<ConversationUploadedFileInfo>(imageCount);
        var fileIds = new List<Guid>(imageCount);
        for (var i = 0; i < imageCount; i++)
        {
            var fileId = Guid.NewGuid();
            fileIds.Add(fileId);
            // The first image keeps the canonical 5-byte body the single-image assertions check; the rest are distinct.
            ReadOnlyMemory<byte> imageBytes = i == 0
                ? new byte[]
                {
                    1,
                    2,
                    3,
                    4,
                    5
                }
                : new byte[]
                {
                    (byte)(10 + i)
                };
            files.Add(new ConversationUploadedFileInfo
            {
                FileId = fileId,
                ConversationId = conversationId,
                OriginalFileName = $"photo{i}.png",
                MimeType = "image/png",
                Extension = ".png",
                SizeBytes = imageBytes.Length,
                ExtractionStatus = DocumentExtractionStatus.Image,
                ExtractedChars = null,
                CreatedAtUtc = 0
            });
            uploadedFileStore.ReadBytesAsync(conversationId, fileId, Arg.Any<CancellationToken>()).Returns<ReadOnlyMemory<byte>?>(imageBytes);
        }

        uploadedFileStore.ListAsync(conversationId, Arg.Any<CancellationToken>()).Returns(files);

        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("llama.cpp");

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(providerResolver: providerResolver,
                    gguf: CreateGgufModelCapabilityResolver(new GgufModelCapabilities(SupportsThinking: false, SupportsTools: supportsTools, SupportsVision: supportsVision))),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(options ?? new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(uploadedFileStore, Options.Create(options ?? new LocalChatAgentOptions())),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "what is in this image?",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           UseLocalTools: useLocalTools,
                           AttachmentFileIds: fileIds)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.True(runner.CaptureObserved, "Expected the runner to observe the package.");
        return runner.CapturedContext;
    }

    internal static async Task<(List<ChatStreamEvent> Events, IReadOnlyList<ConversationMessageDto> CapturedContext)> RunAttachmentEgressAsync(string? cloudModel,
        bool allowCloudModelAccess,
        IReadOnlyList<ExtraUploadedFile>? extraFiles = null,
        bool offerToolsWithoutFileTools = false,
        FakeModelTrustResolver? trustResolver = null,
        IAgentDefinitionResolver? agentResolver = null)
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ContextCapturingInvocationRunner(dispatcher);

        List<ConversationUploadedFileInfo> files =
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
        files.AddRange((extraFiles ?? []).Select(extra => new ConversationUploadedFileInfo
        {
            FileId = Guid.NewGuid(),
            ConversationId = conversationId,
            OriginalFileName = extra.Name,
            MimeType = "application/octet-stream",
            Extension = Path.GetExtension(extra.Name),
            SizeBytes = 128,
            ExtractionStatus = extra.Status,
            ExtractedChars = extra.ExtractedChars,
            CreatedAtUtc = 0
        }));
        var uploadedFileStore = Substitute.For<IConversationUploadedFileStore>();
        uploadedFileStore.ListAsync(conversationId, Arg.Any<CancellationToken>()).Returns(files);
        uploadedFileStore.ReadExtractedMarkdownAsync(conversationId, fileId, Arg.Any<CancellationToken>()).Returns("The launch code is alpha-zero.");

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(agentResolver ?? CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(trustResolver: trustResolver),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = offerToolsWithoutFileTools
            }),
            StubNodeRuntimeSettings.Create().WithAllowCloudModelAccess(allowCloudModelAccess).WithEnableTools(offerToolsWithoutFileTools).Build(),
            new NodeChatStreamCancellationRegistry(),
            offerToolsWithoutFileTools ? CreateOfferProvider(CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}")) : CreateOfferProvider(),
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

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "what is the launch code?",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           Model: cloudModel,
                           UseLocalTools: offerToolsWithoutFileTools,
                           AttachmentFileIds: [.. files.Select(static file => file.FileId)])))
        {
            events.Add(streamEvent);
        }

        AssertEx.True(runner.CaptureObserved, "Expected the runner to observe the package.");
        return (events, runner.CapturedContext);
    }

    // Runs one bound-agent send whose orchestration resolver returns the given resolution, and returns the streamed events.
    internal static async Task<List<ChatStreamEvent>> RunOrchestrationDegradeAsync(AgentDefinitionKind kind,
        OrchestrationResolution resolution,
        bool playbookWithheld = false,
        string? modelProfile = null,
        IReadOnlyList<CloudWithheldTool>? resolvedCloudWithheldTools = null)
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var agentDefinitionId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { }, agentDefinitionId);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ContextCapturingInvocationRunner(dispatcher);

        var store = Substitute.For<IAgentDefinitionStore>();
        store.GetByIdAsync(agentDefinitionId, Arg.Any<CancellationToken>()).Returns(CreateOrchestratorRecord(agentDefinitionId));
        var agentDefinitionResolver = Substitute.For<IAgentDefinitionResolver>();
        agentDefinitionResolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                               .Returns(new ResolvedAgentRuntime("Agent persona.", [], modelProfile, ReasoningEffort: null, AgentDefinitionVersion: 4, agentDefinitionId, "Agent", Kind: kind,
                                   PlaybookWithheld: playbookWithheld, CloudWithheldTools: resolvedCloudWithheldTools));
        var orchestrationResolver = Substitute.For<IOrchestrationResolver>();
        orchestrationResolver.ResolveAsync(Arg.Any<AgentDefinitionRecord>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(resolution);

        var service = CreateServiceWithScopeFactory(persistence,
            runner,
            dispatcher,
            CreateScopeFactory(),
            allowCloudModelAccess: false,
            agentDefinitionResolver,
            store,
            orchestrationResolver);

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId, "hello", MessageId: assistantMessageId, RequestId: requestId)))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    internal static async Task<(List<ChatStreamEvent> Events, IReadOnlyList<ConversationMessageDto> CapturedContext)> RunOrchestrationAttachmentEgressAsync(bool anyParticipantNeedsCloudDataSwitch,
        bool allowCloudModelAccess)
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var agentDefinitionId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { }, agentDefinitionId);
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

        var store = Substitute.For<IAgentDefinitionStore>();
        store.GetByIdAsync(agentDefinitionId, Arg.Any<CancellationToken>()).Returns(CreateOrchestratorRecord(agentDefinitionId));
        // The chat-turn resolver gates the orchestration reload on the resolved runtime's Kind, so the resolver
        // must surface Kind=Orchestrator for this bound agent.
        var agentDefinitionResolver = Substitute.For<IAgentDefinitionResolver>();
        agentDefinitionResolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                               .Returns(new ResolvedAgentRuntime("Orchestrator persona.", [], ModelProfile: null, ReasoningEffort: null, AgentDefinitionVersion: 4,
                                   agentDefinitionId, "Orchestrator", Kind: AgentDefinitionKind.Orchestrator));
        var orchestrationResolver = Substitute.For<IOrchestrationResolver>();
        orchestrationResolver.ResolveAsync(Arg.Any<AgentDefinitionRecord>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                             .Returns(OrchestrationResolution.Compiled(new ResolvedOrchestration
                             {
                                 Spec = CreateSampleSpec(),
                                 ResolvedSystemPrompt = "Orchestrator prompt.",
                                 ModelProfile = "qwen3:8b",
                                 ReasoningEffort = null,
                                 AgentDefinitionVersion = 4,
                                 AnyParticipantNeedsCloudDataSwitch = anyParticipantNeedsCloudDataSwitch,
                                 FirstUngrantedCloudParticipantModel = anyParticipantNeedsCloudDataSwitch ? "azure-specialist-deploy" : null
                             }));

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(agentDefinitionResolver, store, orchestrationResolver, CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().WithAllowCloudModelAccess(allowCloudModelAccess).Build(),
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

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "summarize the attachment",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           AttachmentFileIds: [fileId])))
        {
            events.Add(streamEvent);
        }

        AssertEx.True(runner.CaptureObserved, "Expected the runner to observe the package.");
        return (events, runner.CapturedContext);
    }

    internal static IPlaybookActionStore CreateEmptyPlaybookStore()
    {
        var playbookStore = Substitute.For<IPlaybookActionStore>();
        playbookStore.ListEnabledByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        return playbookStore;
    }

    internal static IAgentSkillStore CreateEmptySkillStore()
    {
        var skillStore = Substitute.For<IAgentSkillStore>();
        skillStore.ListEnabledByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<IReadOnlyList<AgentSkillRecord>>([]));
        return skillStore;
    }

    internal static string LoadEmbeddedChatPrompt()
    {
        var assembly = typeof(LocalChatAgentOptions).Assembly;
        var resourceName = new LocalChatAgentOptions().InstructionsResource;
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Embedded instructions resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static ILocalToolOfferProvider CreateOfferProvider(params AllowedToolDto[] tools)
    {
        var provider = Substitute.For<ILocalToolOfferProvider>();
        // Allow-listed, so a tool turn raises no allow-list ToolsWithheld notice unless a test opts out.
        provider.IsToolCapable(Arg.Any<string?>()).Returns(true);
        provider.GetOfferedTools(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<ExternalProviderCloudGrants?>()).Returns(tools);
        provider.GetOfferedToolsAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<ExternalProviderCloudGrants?>(), Arg.Any<CancellationToken>()).Returns(tools);
        provider.GetOfferedToolsForProfileAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<ExternalProviderCloudGrants?>(), Arg.Any<CancellationToken>()).Returns(tools);
        provider.GetCloudWithheldToolsAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<ExternalProviderCloudGrants?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<CloudWithheldTool>>([]));
        return provider;
    }

    // A real fence-seed provider over a fixed test key so any attachment-composing test gets a valid (non-null) seed.
    // The holder owns no unmanaged resource (its Dispose is a no-op over a fixed byte array), so a process-lifetime
    // static instance needs no cleanup method.
#pragma warning disable TUnit0023
    internal static readonly INodeSqliteKeyHolder FenceKeyHolder = new StaticFenceKeyHolder();

#pragma warning restore TUnit0023

    internal static IChatTurnContextBuilder CreateTurnContextBuilder(IConversationUploadedFileStore? uploadedFileStore = null,
        IOptions<LocalChatAgentOptions>? localChatOptions = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        return new ChatTurnContextBuilder(uploadedFileStore ?? Substitute.For<IConversationUploadedFileStore>(),
            CreateFenceSeedProvider(),
            scopeFactory ?? CreateScopeFactory(),
            localChatOptions ?? Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            NullLogger<ChatTurnContextBuilder>.Instance);
    }

    internal static IUntrustedContentFenceSeedProvider CreateFenceSeedProvider()
    {
        return new UntrustedContentFenceSeedProvider(FenceKeyHolder);
    }

    // A scope factory for the plain-chat knowledge-base retrieval path. When no search service is supplied the factory
    // only satisfies the ctor (tests that leave UseKnowledgeBase off never resolve a scope). When one is supplied the
    // factory hands out a scope whose provider resolves it, so the KB grounding path can be exercised end to end.
    internal static IServiceScopeFactory CreateScopeFactory(IKnowledgeSearchService? searchService = null)
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

    // A knowledge search service returning a fixed hit list, wired into a scope factory for the KB grounding tests.
    internal static IServiceScopeFactory CreateKnowledgeScopeFactory(params KnowledgeSearchHit[] hits)
    {
        var searchService = Substitute.For<IKnowledgeSearchService>();
        searchService.SearchAsync(Arg.Any<KnowledgeSearchRequest>(), Arg.Any<CancellationToken>())
                     .Returns(new KnowledgeSearchResult
                     {
                         Results = hits
                     });
        return CreateScopeFactory(searchService);
    }

    internal static KnowledgeSearchHit KnowledgeHit(string title, string content, double score)
    {
        return new KnowledgeSearchHit
        {
            DocumentId = Guid.NewGuid(),
            ChunkId = Guid.NewGuid(),
            Title = title,
            Section = "Section",
            Content = content,
            Source = "knowledge-base",
            Score = score,
            ChunkIndex = 0,
            DocumentStatus = KnowledgeDocumentStatus.Indexed,
            ServingLastKnownGood = false
        };
    }

    internal static NodeChatStreamService CreateAgentHomeService(INodeChatPersistenceService persistence,
        IInvocationRunner runner,
        RecordingWorkerEventDispatcher dispatcher,
        IConversationSandboxStager stager,
        ILocalChatRuntimePackageBuilder? runtimePackageBuilder = null,
        IGraphWorkflowStore? graphWorkflows = null)
    {
        return new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            runtimePackageBuilder ?? new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = true
            }),
            StubNodeRuntimeSettings.Create().WithEnableTools(true).Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(CreateLocalToolDto("read_file", "{\"type\":\"object\"}")),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(),
            stager,
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            graphWorkflows ?? Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());
    }

    // Builds a NodeChatStreamService wired with the given scope factory (for the knowledge-base retrieval path), mirroring
    // the default-harness construction used by the attachment-egress helper. cloud-vs-local is chosen by the Model passed
    // to the send, not here; allowCloudModelAccess seeds the KB egress gate.
    internal static NodeChatStreamService CreateServiceWithScopeFactory(INodeChatPersistenceService persistence,
        ContextCapturingInvocationRunner runner,
        RecordingWorkerEventDispatcher dispatcher,
        IServiceScopeFactory scopeFactory,
        bool allowCloudModelAccess = false,
        IAgentDefinitionResolver? agentDefinitionResolver = null,
        IAgentDefinitionStore? agentDefinitionStore = null,
        IOrchestrationResolver? orchestrationResolver = null,
        IChatTurnContextBuilder? turnContextBuilder = null,
        IGpuWorkGate? gpuWorkGate = null)
    {
        return new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(agentDefinitionResolver ?? CreateAgentDefinitionResolver(),
                agentDefinitionStore ?? CreateAgentDefinitionStore(),
                orchestrationResolver ?? CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().WithAllowCloudModelAccess(allowCloudModelAccess).Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            turnContextBuilder ?? CreateTurnContextBuilder(scopeFactory: scopeFactory),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            gpuWorkGate ?? new GpuWorkGate());
    }

    /// <summary>An extra uploaded file for the attachment-egress harness: its name, extraction status and extracted size.</summary>
    internal readonly record struct ExtraUploadedFile(string Name, DocumentExtractionStatus Status, int? ExtractedChars);

    internal sealed class StaticFenceKeyHolder : INodeSqliteKeyHolder
    {
        public ReadOnlyMemory<byte> Key { get; } = new byte[32];

        public void Dispose()
        {
        }
    }

    internal sealed class TrackingAgentHomeExecutionLease : IAgentHomeExecutionLease
    {
        private readonly IAgentHomeExecutionLease? _inner;

        public TrackingAgentHomeExecutionLease(IAgentHomeExecutionLease? inner = null)
        {
            _inner = inner;
        }

        public bool IsBorrowed => _inner?.IsBorrowed ?? false;

        public int DisposeCount { get; private set; }

        public int AmbientScopeEnterCount { get; private set; }

        public int AmbientScopeDisposeCount { get; private set; }

        public IDisposable EnterAmbientScope()
        {
            AmbientScopeEnterCount++;
            var ambient = _inner?.EnterAmbientScope();
            return new CallbackDisposable(() =>
            {
                ambient?.Dispose();
                AmbientScopeDisposeCount++;
            });
        }

        public void Dispose()
        {
            DisposeCount++;
            _inner?.Dispose();
        }

        private sealed class CallbackDisposable : IDisposable
        {
            private Action? _callback;

            public CallbackDisposable(Action callback)
            {
                _callback = callback;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _callback, null)?.Invoke();
            }
        }
    }

    internal sealed class StubAgentHomeIdentityProvider : IAgentHomeIdentityProvider
    {
        private readonly string _owner;
        private readonly string _node;

        public StubAgentHomeIdentityProvider(string owner, string node)
        {
            _owner = owner;
            _node = node;
        }

        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentHomeOwnerIdentity
            {
                OwnerUserId = _owner,
                NodeId = _node
            });
        }
    }

    // The default node-settings store: no operator-selected node default, so model resolution falls through to the
    // request model (or the static config fallback). The capability-gate test supplies a tool-capable default.
    internal static INodeSettingsStore CreateNodeSettingsStore(string? defaultModelName = null,
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
    // think/tool-offer assertions stay byte-identical (these tests pre-date per-model capability gating). The dedicated
    // capability-gate tests substitute an incapable classification.
    // These tests exercise the Ollama /api/show classification path, so route every model to the Ollama provider; the
    // chat service only skips classification for non-Ollama (e.g. llama.cpp/GGUF) providers.
    internal static ILocalModelProviderResolver CreateLocalModelProviderResolver()
    {
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(OllamaLocalModelProvider.OllamaProviderName);
        return resolver;
    }

    // ChatTurnResolver resolves capabilities through the shared IModelCapabilityResolver, so these tests compose the
    // real one over substituted collaborators — the routing decision under test lives in that one place.
    internal static IModelCapabilityResolver CreateModelCapabilityResolver(IModelClassificationService? classification = null,
        ILocalModelProviderResolver? providerResolver = null,
        IGgufModelCapabilityResolver? gguf = null,
        FakeModelTrustResolver? trustResolver = null)
    {
        return new ModelCapabilityResolver(classification ?? CreateModelClassificationService(),
            providerResolver ?? CreateLocalModelProviderResolver(),
            gguf ?? CreateGgufModelCapabilityResolver(),
            trustResolver ?? new FakeModelTrustResolver());
    }

    // The default resolver reports every model as not-a-GGUF (null), so the existing Ollama-routed tests keep their
    // /api/show classification behavior. A llama.cpp-capability test overrides ResolveOrNullAsync explicitly.
    internal static IGgufModelCapabilityResolver CreateGgufModelCapabilityResolver(GgufModelCapabilities? capabilities = null)
    {
        var resolver = Substitute.For<IGgufModelCapabilityResolver>();
        resolver.ResolveOrNullAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(capabilities);
        return resolver;
    }

    // The default local-default resolver resolves to an installed GGUF chat model so a "Local runtime default" send
    // proceeds (these tests are not about the no-model path): it ECHOES the persisted node default when one is set
    // (so the operator-node-default offer assertion stays green) and otherwise falls back to the static config model
    // (a stand-in installed GGUF). The dedicated no-model test passes resolved=null + echoPersistedDefault=false to
    // force the empty result; the routes-installed-GGUF test passes a specific resolved name.
    internal static ILocalDefaultChatModelResolver CreateLocalDefaultChatModelResolver(string? resolved = null, bool echoPersistedDefault = true)
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

    // No-op extraction dispatcher: these tests are not about post-run memory (the playbook-disabled default agent never
    // fires the hook anyway). A substitute's void Dispatch is a no-op, keeping the send/regenerate SSE assertions intact.
    internal static IMemoryExtractionDispatcher CreateMemoryExtractionDispatcher()
    {
        return Substitute.For<IMemoryExtractionDispatcher>();
    }

    internal static IModelClassificationService CreateModelClassificationService(params string[] capabilities)
    {
        var resolved = capabilities.Length > 0 ? capabilities : ["completion", "tools", "thinking"];
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
                           map[modelName] = new ModelClassificationResult
                           {
                               ModelName = modelName,
                               Kind = ModelKind.Chat,
                               DetectedKind = ModelKind.Chat,
                               Capabilities = resolved,
                               IsOverridden = false
                           };
                       }
                   }

                   return Task.FromResult<IReadOnlyDictionary<string, ModelClassificationResult>>(map);
               });
        return service;
    }

    // The default (unbound) resolver: ResolveAsync returns null, so the service keeps today's literals — these tests
    // exercise the default chat persona. Bound-agent behavior is covered by the dedicated bound-conversation tests.
    internal static IAgentDefinitionResolver CreateAgentDefinitionResolver()
    {
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns((ResolvedAgentRuntime?)null);
        return resolver;
    }

    // The default-agent provider: returns null so the effective-agent precedence falls through to a null id (no seeded
    // Default Assistant in these unit tests), keeping the default-persona contract — the resolver is consulted with a
    // null binding exactly as before. The dedicated default-fallback test supplies a non-null id.
    internal static IDefaultAgentProvider CreateDefaultAgentProvider(Guid? defaultAgentId = null)
    {
        var provider = Substitute.For<IDefaultAgentProvider>();
        provider.GetDefaultAgentIdAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(defaultAgentId));
        return provider;
    }

    // The default store/orchestration resolver: GetByIdAsync returns null so ResolveOrchestrationAsync never reaches the
    // orchestration resolver — the package carries no spec and the single-agent path is byte-identical.
    // The default store reads back nothing (the orchestration reload's null-record path). A pinnedModel supplies the one
    // definition the turn resolver reads when there is no active model, so the pin can be the capability head.
    internal static IAgentDefinitionStore CreateAgentDefinitionStore(Guid? pinningDefinitionId = null, string? pinnedModel = null)
    {
        var store = Substitute.For<IAgentDefinitionStore>();
        store.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((AgentDefinitionRecord?)null);
        if (pinningDefinitionId is { } definitionId && pinnedModel is not null)
        {
            store.GetByIdAsync(definitionId, Arg.Any<CancellationToken>())
                 .Returns(new AgentDefinitionRecord
                 {
                     Id = definitionId,
                     Name = "Pinned Agent",
                     Description = null,
                     Instructions = "Pinned persona.",
                     ModelProfile = pinnedModel,
                     ReasoningEffort = null,
                     Kind = AgentDefinitionKind.Single,
                     AllowedToolNames = [],
                     ToolApprovals = new Dictionary<string, bool>(),
                     OrchestrationTopologyJson = null,
                     Version = 9,
                     CreatedAtUtc = 10,
                     UpdatedAtUtc = 10
                 });
        }

        return store;
    }

    internal static IOrchestrationResolver CreateOrchestrationResolver()
    {
        var resolver = Substitute.For<IOrchestrationResolver>();
        resolver.ResolveAsync(Arg.Any<AgentDefinitionRecord>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(OrchestrationResolution.NotOrchestrated);
        return resolver;
    }

    internal static AgentDefinitionRecord CreateOrchestratorRecord(Guid id)
    {
        return new AgentDefinitionRecord
        {
            Id = id,
            Name = "Orchestrator",
            Description = null,
            Instructions = "Orchestrator prompt.",
            ModelProfile = "qwen3:8b",
            ReasoningEffort = null,
            Kind = AgentDefinitionKind.Orchestrator,
            AllowedToolNames = [],
            ToolApprovals = new Dictionary<string, bool>(),
            OrchestrationTopologyJson = null,
            Version = 4,
            CreatedAtUtc = 10,
            UpdatedAtUtc = 10
        };
    }

    internal static OrchestrationSpec CreateSampleSpec()
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
                    // Participants carry their OWN projected offer, which is the list the ask_user withdrawal has to
                    // reach — the send's single allowed-tool list never sees them.
                    Tools = [CreateLocalToolDto(AskUserTool.ToolName, "{\"type\":\"object\"}")]
                },
                new OrchestrationSpecParticipant
                {
                    Key = "b",
                    Name = "Specialist",
                    Instructions = "Specialist.",
                    ModelId = "qwen3:8b",
                    Tools =
                    [
                        CreateLocalToolDto("Calculate", "{\"type\":\"object\"}"),
                        CreateLocalToolDto(AskUserTool.ToolName, "{\"type\":\"object\"}"),
                        CreateLocalToolDto(WebSearchToolDefinition.ToolName, "{\"type\":\"object\"}")
                    ]
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

    // Stand-in offered tools default to a concrete ReadLocal category (never Unknown) to mirror the production invariant
    // that every real offered tool declares a category; a test needing another category passes it explicitly.
    internal static AllowedToolDto CreateLocalToolDto(string name, string parameterSchema, ToolCategory category = ToolCategory.ReadLocal)
    {
        return new AllowedToolDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = ToolLocation.ClientLocal,
            ParameterSchema = parameterSchema,
            RequiresApproval = false,
            Category = category
        };
    }

    internal static IReadOnlyList<NodeChatPersistedMessageDto> ToolInvestigationTurn(Guid conversationId) =>
    [
        AnchorScenarioMessage(conversationId, Guid.NewGuid(), sequence: 1, "user", "How does Classify work?"),
        AnchorScenarioMessage(conversationId, Guid.NewGuid(), sequence: 2, "assistant", "It maps exceptions to reasons.") with
        {
            Parts =
            [
                new NodeChatMessagePart(NodeChatMessagePartKinds.Tool,
                    1,
                    Text: null,
                    ToolCallId: "call-1",
                    Name: "mcp__codegraph__explore",
                    State: NodeChatToolPartStates.Received,
                    Args: "{\"query\":\"Classify\"}",
                    Result: "Classify maps HttpRequestException " + new string('x', 20_000),
                    RequiresApproval: true)
            ]
        }
    ];

    internal static NodeChatPersistedMessageDto AnchorScenarioMessage(Guid conversationId,
        Guid messageId,
        int sequence,
        string role,
        string content,
        Guid? variantGroupId = null)
    {
        return new NodeChatPersistedMessageDto
        {
            MessageId = messageId,
            ConversationId = conversationId,
            RequestId = null,
            Sequence = sequence,
            Role = role,
            Content = content,
            Reasoning = null,
            Status = NodeChatMessageStatusValues.Completed,
            CreatedAtUtc = sequence,
            UpdatedAtUtc = sequence,
            Model = null,
            Error = null,
            MetadataJson = null,
            VariantGroupId = variantGroupId
        };
    }

    internal static async Task<ContextCapturingInvocationRunner> RunWithVariantConversationAsync(Guid conversationId,
        Guid variantGroupId,
        Guid olderVariantId,
        Guid newerVariantId,
        IReadOnlyDictionary<Guid, Guid>? persistedSelection,
        IReadOnlyDictionary<Guid, Guid>? requestSelection,
        string? compactionSummary = null,
        int? compactionCoversToSequence = null,
        IReadOnlyList<NodeChatPersistedMessageDto>? conversationMessages = null,
        string? model = null)
    {
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreateVariantPersistence(conversationId,
            assistantMessageId,
            requestId,
            variantGroupId,
            olderVariantId,
            newerVariantId,
            persistedSelection,
            compactionSummary,
            compactionCoversToSequence,
            conversationMessages);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ContextCapturingInvocationRunner(dispatcher);
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
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());

        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "follow up",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           Model: model,
                           SelectedPath: requestSelection)))
        {
            runner.Events.Add(streamEvent);
        }

        AssertEx.True(runner.Events.Count > 0, "Expected the send to stream events.");
        AssertEx.True(runner.CaptureObserved, "Expected the invocation runner to observe the runtime package.");
        runner.SelectionPersisted = persistence.ReceivedCalls()
                                               .Any(call => call.GetMethodInfo().Name == nameof(INodeChatPersistenceService.SetSelectedPathAsync));
        return runner;
    }

    internal static INodeChatPersistenceService CreateVariantPersistence(Guid conversationId,
        Guid assistantMessageId,
        Guid requestId,
        Guid variantGroupId,
        Guid olderVariantId,
        Guid newerVariantId,
        IReadOnlyDictionary<Guid, Guid>? persistedSelection,
        string? compactionSummary = null,
        int? compactionCoversToSequence = null,
        IReadOnlyList<NodeChatPersistedMessageDto>? conversationMessages = null)
    {
        var persistence = Substitute.For<INodeChatPersistenceService>();

        var userTurn = new NodeChatPersistedMessageDto
        {
            MessageId = Guid.NewGuid(),
            ConversationId = conversationId,
            RequestId = null,
            Sequence = 0,
            Role = "user",
            Content = "original question",
            Reasoning = null,
            Status = NodeChatMessageStatusValues.Completed,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1,
            Model = null,
            Error = null,
            MetadataJson = null
        };
        var olderVariant = new NodeChatPersistedMessageDto
        {
            MessageId = olderVariantId,
            ConversationId = conversationId,
            RequestId = Guid.NewGuid(),
            Sequence = 1,
            Role = "assistant",
            Content = "older answer",
            Reasoning = null,
            Status = NodeChatMessageStatusValues.Completed,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1,
            Model = null,
            Error = null,
            MetadataJson = null,
            VariantGroupId = variantGroupId
        };
        var newerVariant = new NodeChatPersistedMessageDto
        {
            MessageId = newerVariantId,
            ConversationId = conversationId,
            RequestId = Guid.NewGuid(),
            Sequence = 2,
            Role = "assistant",
            Content = "newer answer",
            Reasoning = null,
            Status = NodeChatMessageStatusValues.Completed,
            CreatedAtUtc = 2,
            UpdatedAtUtc = 2,
            Model = null,
            Error = null,
            MetadataJson = null,
            VariantGroupId = variantGroupId
        };

        var conversation = new NodeChatConversationDto
        {
            ConversationId = conversationId,
            Title = "variant chat",
            UserId = null,
            CreatedAtUtc = 1,
            LastSeenUtc = 1,
            Purged = false,
            Messages = conversationMessages ?? [userTurn, olderVariant, newerVariant],
            SelectedPath = persistedSelection,
            CompactionSummary = compactionSummary,
            CompactionSummaryCoversToSequence = compactionCoversToSequence
        };
        // The just-sent turn always takes the next free PHYSICAL sequence, exactly like NextSequenceAsync does.
        var newUserMessage = new NodeChatPersistedMessageDto
        {
            MessageId = Guid.NewGuid(),
            ConversationId = conversationId,
            RequestId = null,
            Sequence = conversation.Messages.Max(message => message.Sequence) + 1,
            Role = "user",
            Content = "follow up",
            Reasoning = null,
            Status = NodeChatMessageStatusValues.Completed,
            CreatedAtUtc = 3,
            UpdatedAtUtc = 3,
            Model = null,
            Error = null,
            MetadataJson = null
        };
        var assistantPending = CreateAssistantMessage(conversationId, assistantMessageId, requestId, NodeChatMessageStatusValues.Pending, string.Empty, reasoning: null);

        persistence.GetConversationAsync(conversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        // Persisting a selection CLEARS the stored compaction synopsis (NodeChatConversationCommands.SetSelectedPathAsync
        // writes literal NULLs), so the stub models that write: a read after the switch must not still see the synopsis.
        // Without it the fixed DTO would hide whether the service reads before or after persisting.
        var selectionPersisted = false;
        persistence.SetSelectedPathAsync(Arg.Any<NodeChatSetSelectedPathRequest>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo =>
                   {
                       selectionPersisted = true;
                       return callInfo.ArgAt<NodeChatSetSelectedPathRequest>(0).SelectedPath ?? new Dictionary<Guid, Guid>();
                   });
        // The send path reads through GetConversationForTurnAsync. An unstubbed substitute returns null there, and the
        // service then throws NodeChatConversationNotFoundException before any behavior under test runs.
        persistence.GetConversationForTurnAsync(conversationId, Arg.Any<CancellationToken>())
                   .Returns(_ => selectionPersisted
                       ? conversation with
                       {
                           CompactionSummary = null,
                           CompactionSummaryCoversToSequence = null
                       }
                       : conversation);
        persistence.PersistUserMessageAsync(Arg.Any<NodeChatPersistUserMessageRequest>(), Arg.Any<CancellationToken>()).Returns(newUserMessage);
        persistence.CreateAssistantPlaceholderAsync(Arg.Any<NodeChatCreateAssistantPlaceholderRequest>(), Arg.Any<CancellationToken>()).Returns(assistantPending);
        persistence.MarkAssistantQueuedAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                   .Returns(assistantPending with
                   {
                       Status = NodeChatMessageStatusValues.Queued
                   });
        persistence.MarkAssistantStreamingAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                   .Returns(assistantPending with
                   {
                       Status = NodeChatMessageStatusValues.Streaming
                   });
        persistence.FlushAssistantPartialAsync(Arg.Any<NodeChatPartialFlushRequest>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo => CreateAssistantMessage(conversationId, assistantMessageId, requestId, NodeChatMessageStatusValues.Streaming,
                       callInfo.ArgAt<NodeChatPartialFlushRequest>(0).Content, reasoning: null));
        persistence.TerminalizeAssistantMessageAsync(Arg.Any<NodeChatTerminalizeMessageRequest>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo => CreateAssistantMessage(conversationId, assistantMessageId, requestId, callInfo.ArgAt<NodeChatTerminalizeMessageRequest>(0).Status,
                       callInfo.ArgAt<NodeChatTerminalizeMessageRequest>(0).Content ?? string.Empty, reasoning: null));

        return persistence;
    }

    internal static InvocationApprovalState ParkedApproval() =>
        new()
        {
            RequestId = "approval-1",
            Description = "Run a command",
            RequestedAt = DateTimeOffset.UtcNow,
            CallId = "call-1",
            ToolName = "run_command",
            Arguments = "{}",
            SessionScopeEligible = false
        };

    internal static INodeChatPersistenceService CreatePersistence(Guid conversationId,
        Guid assistantMessageId,
        Guid requestId,
        Action<NodeChatTerminalizeMessageRequest> terminalized,
        Guid? agentDefinitionId = null,
        Action<NodeChatCreateAssistantPlaceholderRequest>? placeholderObserver = null)
    {
        var persistence = Substitute.For<INodeChatPersistenceService>();
        var conversation = new NodeChatConversationDto
        {
            ConversationId = conversationId,
            Title = "test",
            UserId = null,
            CreatedAtUtc = 1,
            LastSeenUtc = 1,
            Purged = false,
            Messages = [],
            AgentDefinitionId = agentDefinitionId
        };
        var userMessage = new NodeChatPersistedMessageDto
        {
            MessageId = Guid.NewGuid(),
            ConversationId = conversationId,
            RequestId = null,
            Sequence = 1,
            Role = "user",
            Content = "hello",
            Reasoning = null,
            Status = NodeChatMessageStatusValues.Completed,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1,
            Model = null,
            Error = null,
            MetadataJson = null
        };
        var assistantPending = CreateAssistantMessage(conversationId,
            assistantMessageId,
            requestId,
            NodeChatMessageStatusValues.Pending,
            string.Empty,
            reasoning: null);
        var assistantQueued = assistantPending with
        {
            Status = NodeChatMessageStatusValues.Queued
        };
        var assistantStreaming = assistantPending with
        {
            Status = NodeChatMessageStatusValues.Streaming
        };

        persistence.GetConversationAsync(conversationId, Arg.Any<CancellationToken>())
                   .Returns(conversation);
        // The send path reads through GetConversationForTurnAsync. An unstubbed substitute returns null there, and the
        // service then throws NodeChatConversationNotFoundException before any behavior under test runs.
        persistence.GetConversationForTurnAsync(conversationId, Arg.Any<CancellationToken>())
                   .Returns(conversation);
        persistence.PersistUserMessageAsync(Arg.Any<NodeChatPersistUserMessageRequest>(), Arg.Any<CancellationToken>())
                   .Returns(userMessage);
        persistence.CreateAssistantPlaceholderAsync(Arg.Any<NodeChatCreateAssistantPlaceholderRequest>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo =>
                   {
                       var request = callInfo.ArgAt<NodeChatCreateAssistantPlaceholderRequest>(0);
                       placeholderObserver?.Invoke(request);
                       // Reflect the stamped attribution into the returned pending DTO so the AssistantPending event
                       // carries the agent name a real placeholder would.
                       return assistantPending with
                       {
                           AgentDefinitionId = request.AgentDefinitionId,
                           AgentName = request.AgentName
                       };
                   });
        persistence.MarkAssistantQueuedAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                   .Returns(assistantQueued);
        persistence.MarkAssistantStreamingAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                   .Returns(assistantStreaming);
        persistence.FlushAssistantPartialAsync(Arg.Any<NodeChatPartialFlushRequest>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo =>
                   {
                       var request = callInfo.ArgAt<NodeChatPartialFlushRequest>(0);
                       return CreateAssistantMessage(conversationId,
                           assistantMessageId,
                           requestId,
                           NodeChatMessageStatusValues.Streaming,
                           request.Content,
                           request.Reasoning);
                   });
        persistence.TerminalizeAssistantMessageAsync(Arg.Do<NodeChatTerminalizeMessageRequest>(terminalized), Arg.Any<CancellationToken>())
                   .Returns(callInfo =>
                   {
                       var request = callInfo.ArgAt<NodeChatTerminalizeMessageRequest>(0);
                       return CreateAssistantMessage(conversationId,
                               assistantMessageId,
                               requestId,
                               request.Status,
                               request.Content ?? string.Empty,
                               request.Reasoning,
                               request.Error) with
                           {
                               FailureCategory = request.FailureCategory
                           };
                   });

        return persistence;
    }

    internal static NodeChatPersistedMessageDto CreateAssistantMessage(Guid conversationId,
        Guid assistantMessageId,
        Guid requestId,
        string status,
        string content,
        string? reasoning,
        string? error = null)
    {
        return new NodeChatPersistedMessageDto
        {
            MessageId = assistantMessageId,
            ConversationId = conversationId,
            RequestId = requestId,
            Sequence = 2,
            Role = "assistant",
            Content = content,
            Reasoning = reasoning,
            Status = status,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1,
            Model = null,
            Error = error,
            MetadataJson = null
        };
    }

    internal sealed class StreamingUntilCancelledInvocationRunner : IInvocationRunner
    {
        private readonly RecordingWorkerEventDispatcher _dispatcher;

        public StreamingUntilCancelledInvocationRunner(RecordingWorkerEventDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            await _dispatcher.ReportInvocationThinkingChunkAsync(context.Package.InvocationId, "thinking");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
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
    }

    internal sealed class GatedCompletingInvocationRunner : IInvocationRunner
    {
        private readonly RecordingWorkerEventDispatcher _dispatcher;
        private readonly Task _release;

        public GatedCompletingInvocationRunner(RecordingWorkerEventDispatcher dispatcher, Task release)
        {
            _dispatcher = dispatcher;
            _release = release;
        }

        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            // Emit a delta so the consumer can disconnect, then block until released to report the real Completed
            // terminal. This reproduces a client disconnecting while the shared runner is still working.
            await _dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, "answer");
            await _release;
            await _dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId, inputTokens: 10, outputTokens: 3, totalTokens: 13, reasoningTokens: 1);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
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
    }

    internal sealed class ContextCapturingInvocationRunner : IInvocationRunner
    {
        private readonly RecordingWorkerEventDispatcher _dispatcher;

        public ContextCapturingInvocationRunner(RecordingWorkerEventDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        // The conversation context assembled onto the runtime package; the selected-path tests assert which
        // variant the service included.
        public IReadOnlyList<ConversationMessageDto> CapturedContext { get; private set; } = [];

        public bool CaptureObserved { get; private set; }

        // Set by RunWithVariantConversationAsync after the run, from a NSubstitute Received() check on the mock —
        // exposed here so the test reads one object.
        public bool SelectionPersisted { get; set; }

        public List<ChatStreamEvent> Events { get; } = [];

        // Hits the run records the way search_knowledge_base does inside the tool loop, so a test sees the tool path's sources.
        public IReadOnlyList<KnowledgeSearchHit> ToolHits { get; init; } = [];

        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            CapturedContext = context.Package.ConversationContext;
            CaptureObserved = true;
            foreach (var hit in ToolHits)
            {
                TurnKnowledgeSourceCollector.Add(hit);
            }

            await _dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, "answer");
            await _dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId, inputTokens: 10, outputTokens: 3, totalTokens: 13, reasoningTokens: 1);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
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
    }

    internal sealed class CompletingInvocationRunner : IInvocationRunner
    {
        private readonly RecordingWorkerEventDispatcher _dispatcher;

        public CompletingInvocationRunner(RecordingWorkerEventDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            await _dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, "answer");
            await _dispatcher.ReportInvocationThinkingChunkAsync(context.Package.InvocationId, "thinking");
            await _dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId, inputTokens: 10, outputTokens: 3, totalTokens: 13, reasoningTokens: 1);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
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
    }

    internal sealed class AwaitingCoderReadInvocationRunner : IInvocationRunner
    {
        private readonly RecordingWorkerEventDispatcher _dispatcher;
        private readonly ICoderWorkspaceReader _reader;

        public AwaitingCoderReadInvocationRunner(RecordingWorkerEventDispatcher dispatcher,
            ICoderWorkspaceReader reader)
        {
            _dispatcher = dispatcher;
            _reader = reader;
        }

        public int ActiveInvocationCount => 0;

        public string ReadResult { get; private set; } = string.Empty;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            ReadResult = await _reader.ReadFileAsync(new ReadFileToolRequest
                {
                    Path = "src/a.txt"
                },
                cancellationToken);
            await _dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, ReadResult);
            await _dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

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
    }

    internal sealed class PackageCapturingInvocationRunner : IInvocationRunner
    {
        private readonly RecordingWorkerEventDispatcher _dispatcher;

        public PackageCapturingInvocationRunner(RecordingWorkerEventDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        // Captures the runtime-package fields the binding hydration drives: the system prompt, the agent-definition
        // version, the reasoning effort, and the offered tool list. The bound/unbound tests assert on these.
        public string? LastSystemPrompt { get; private set; }
        public int LastAgentDefinitionVersion { get; private set; }
        public string? LastReasoningEffort { get; private set; }
        public string? LastModelProfile { get; private set; }
        public IReadOnlyList<AllowedToolDto> LastAllowedTools { get; private set; } = [];
        public OrchestrationSpec? LastOrchestrationSpec { get; private set; }

        // The timeout block carried on the runtime package; the node-settings test asserts the operator's
        // "Maximum message request timeout" reaches the invocation instead of the TimeoutSettings default.
        public TimeoutSettings? LastTimeouts { get; private set; }
        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            LastSystemPrompt = context.Package.ResolvedSystemPrompt;
            LastAgentDefinitionVersion = context.Package.AgentDefinitionVersion;
            LastReasoningEffort = context.Package.ReasoningEffort;
            LastModelProfile = context.Package.ModelProfile;
            LastAllowedTools = context.Package.AllowedTools;
            LastOrchestrationSpec = context.Package.OrchestrationSpec;
            LastTimeouts = context.Package.Timeouts;
            await _dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, "answer");
            await _dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId, inputTokens: 10, outputTokens: 3, totalTokens: 13, reasoningTokens: 1);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
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
    }

    internal sealed class ReasoningCapturingInvocationRunner : IInvocationRunner
    {
        private readonly RecordingWorkerEventDispatcher _dispatcher;

        public ReasoningCapturingInvocationRunner(RecordingWorkerEventDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        // The reasoning effort carried on the runtime package handed to the invocation; the test asserts the
        // value selected on the send request reaches the runtime package (or stays null when none was selected).
        public string? LastReasoningEffort { get; private set; }

        // The offer list carried on the runtime package; the test asserts the local tool catalog reaches the
        // runtime package only when the client opted in.
        public IReadOnlyList<AllowedToolDto> LastAllowedTools { get; private set; } = [];

        // The sampling overrides carried on the runtime package; the test asserts per-send sampling reaches the
        // runtime package (or stays null when none was selected).
        public SamplingOptions? LastSamplingOptions { get; private set; }

        // The model-selection provenance carried on the runtime package; the work-session test asserts a supervised
        // step never hands the dispatcher permission to replace the model.
        public bool LastAllowAutoModelSwap { get; private set; }

        public bool LastAutoAcceptWebContent { get; private set; }

        public bool CaptureObserved { get; private set; }
        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            LastReasoningEffort = context.Package.ReasoningEffort;
            LastAllowedTools = context.Package.AllowedTools;
            LastSamplingOptions = context.Package.SamplingOptions;
            LastAllowAutoModelSwap = context.Package.AllowAutoModelSwap;
            LastAutoAcceptWebContent = context.Package.AutoAcceptWebContent;
            CaptureObserved = true;
            await _dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, "answer");
            await _dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId, inputTokens: 10, outputTokens: 3, totalTokens: 13, reasoningTokens: 1);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
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
    }

    internal sealed class ToolEmittingInvocationRunner : IInvocationRunner
    {
        private readonly RecordingWorkerEventDispatcher _dispatcher;

        public ToolEmittingInvocationRunner(RecordingWorkerEventDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            await _dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
            {
                InvocationId = context.Package.InvocationId,
                ToolCallId = "call-1",
                ToolName = "weather",
                Phase = ToolCallLifecyclePhase.Requested,
                Arguments = "{\"city\":\"berlin\"}",
                RequiresApproval = false
            });
            await _dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
            {
                InvocationId = context.Package.InvocationId,
                ToolCallId = "call-1",
                ToolName = "weather",
                Phase = ToolCallLifecyclePhase.Completed,
                Result = "sunny",
                IsError = false
            });
            await _dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, "answer");
            await _dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId, inputTokens: 10, outputTokens: 3, totalTokens: 13, reasoningTokens: 1);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
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
    }

    internal sealed class RecordingWorkerEventDispatcher : IWorkerEventDispatcher
    {
        public event EventHandler<InvocationStateChangedEventArgs>? InvocationStateChanged;

        public event EventHandler<ToolCallLifecycleChangedEventArgs>? ToolCallLifecycleChanged;

        public event EventHandler<TurnNoticeChangedEventArgs>? TurnNoticeChanged;

        public event EventHandler<ApprovalRequestedChangedEventArgs>? ApprovalRequestedChanged;

        public event EventHandler<UserQuestionRequestedChangedEventArgs>? UserQuestionRequestedChanged;

        private Guid? _slotHolderId;

        public InvocationState? CurrentInvocation { get; private set; }

        public InvocationState? SlotHolder => CurrentInvocation is { } current && current.InvocationId == _slotHolderId && !NodeChatInvocationPump.IsTerminal(current.Status) ? current : null;

        public IReadOnlyList<InvocationState> GetLiveInvocations() =>
            CurrentInvocation is { } current && !NodeChatInvocationPump.IsTerminal(current.Status) ? [current] : [];

        public InvocationLiveSnapshot GetLiveSnapshot() =>
            new()
            {
                Current = SlotHolder,
                Live = GetLiveInvocations()
            };

        public bool ReleaseSlotForPark(Guid invocationId)
        {
            if (_slotHolderId != invocationId)
            {
                return false;
            }

            _slotHolderId = null;
            return true;
        }

        public Task ReportPromptTimedOutAsync(Guid invocationId, string requestId) =>
            Task.CompletedTask;

        public Task ReacquireSlotAsync(Guid invocationId, CancellationToken cancellationToken)
        {
            _slotHolderId = invocationId;
            return Task.CompletedTask;
        }

        public Task DispatchApprovalResolvedAsync(ApprovalResolvedEvent evt, ApprovalScope scope = ApprovalScope.Once)
        {
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> ReportInvocationAssignedAsync(RuntimePackage package, CancellationToken cancellationToken = default)
        {
            _slotHolderId = package.InvocationId;
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

        public Task ReportInvocationTextReclassifiedAsync(Guid invocationId, string content, string reasoningSuffix)
        {
            if (CurrentInvocation is not null)
            {
                CurrentInvocation.StreamedContent = content;
                CurrentInvocation.StreamedThinkingContent += reasoningSuffix;
            }

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

        public Task ReportInvocationPhaseAsync(Guid invocationId, InvocationRuntimePhase phase, InvocationRuntimePhaseDetail? detail = null)
        {
            if (CurrentInvocation is not null)
            {
                // Stamped on a real transition only, exactly as the real dispatcher does, so a stream driven through
                // this double carries the same phase timestamp the wire contract promises.
                if (CurrentInvocation.RuntimePhase != phase)
                {
                    CurrentInvocation.RuntimePhaseChangedAtUtc = DateTimeOffset.UtcNow;
                }

                CurrentInvocation.RuntimePhase = phase;
                CurrentInvocation.RuntimePhaseDetail = detail;
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

        public Task ReportTurnContextSnapshotAsync(Guid invocationId, ProviderRoundContextSnapshot snapshot)
        {
            if (CurrentInvocation is not null)
            {
                CurrentInvocation.ContextSnapshot = snapshot;
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

        public Task ReportTurnContextWindowAsync(Guid invocationId, int contextCapacityTokens, int reservedOutputTokens)
        {
            if (CurrentInvocation is not null)
            {
                CurrentInvocation.ContextCapacityTokens = contextCapacityTokens;
                CurrentInvocation.ReservedOutputTokens = reservedOutputTokens;
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
