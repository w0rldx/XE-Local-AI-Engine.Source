namespace XE_Local_AI_Engine.Tests.Chat;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Coder.Tools;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The AgentHome owner-node lease a chat turn stages under must come back when the run ends in an expired
///     approval, whether the runner reports that failure (its production shape) or the run task itself faults.
/// </summary>
/// <remarks>
///     Live QA read the "workspace is busy" failures after an expired approval as a leaked lease; the host log showed
///     each one overlapping another live turn. This pins the release path with the REAL lease manager, so a regression
///     that strands the lease after a failed run fails here instead of on an operator's node until restart.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class NodeChatStreamServiceSandboxReleaseTests
{
    private static readonly AgentHomeExecutionLeaseKey LeaseKey = new("owner", "node");

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The preparation transfers lease ownership to the chat service, whose release this test asserts.")]
    public async Task SendMessageAsync_WhenTheRunEndsInAnExpiredApproval_ReleasesTheAgentHomeLease(bool runFaults)
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId);
        var dispatcher = new WorkerEventDispatcher(Substitute.For<IInvocationRunner>(),
            Substitute.For<IInvocationHistory>(),
            NullLogger<WorkerEventDispatcher>.Instance,
            TimeProvider.System);
        var leases = new AgentHomeExecutionLeaseManager();
        var ownedLease = await AcquireOutsideThisContextAsync(leases);
        var stager = Substitute.For<IConversationSandboxStager>();
        stager.PrepareConversationAttachmentsAsync(conversationId, Arg.Any<CancellationToken>())
              .Returns(new ConversationSandboxPreparation([], AssertEx.NotNull(ownedLease)));
        var service = CreateService(persistence, new ExpiredApprovalInvocationRunner(dispatcher, runFaults), dispatcher, stager);

        AssertEx.True(leases.IsHeld(LeaseKey), "the staged turn starts out holding the lease");

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "run a command",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           UseLocalTools: true)))
        {
            events.Add(streamEvent);
        }

        AssertEx.True(events.Any(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantFailed), "the expired approval must fail the turn");
        AssertEx.False(leases.IsHeld(LeaseKey), "the lease must be released once the failed run has drained");
        using var next = await AcquireOutsideThisContextAsync(leases);
        AssertEx.NotNull(next, "the next AgentHome turn must be able to stage");
        AssertEx.False(next!.IsBorrowed);
    }

    // Acquired on a separate async flow so the manager's ambient scope never leaks into the test's own context, where a
    // later acquire would borrow instead of proving the gate is free.
    private static async Task<IAgentHomeExecutionLease?> AcquireOutsideThisContextAsync(AgentHomeExecutionLeaseManager leases)
    {
        await Task.Yield();
        return leases.TryAcquire(LeaseKey);
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The fence key holder is a substitute with no resources; the seed provider owns it for the service's lifetime.")]
    private static NodeChatStreamService CreateService(INodeChatPersistenceService persistence,
        IInvocationRunner runner,
        IWorkerEventDispatcher dispatcher,
        IConversationSandboxStager stager)
    {
        var agentResolver = Substitute.For<IAgentDefinitionResolver>();
        agentResolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                     .Returns((ResolvedAgentRuntime?)null);
        var agentStore = Substitute.For<IAgentDefinitionStore>();
        agentStore.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((AgentDefinitionRecord?)null);
        var orchestration = Substitute.For<IOrchestrationResolver>();
        orchestration.ResolveAsync(Arg.Any<AgentDefinitionRecord>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                     .Returns(OrchestrationResolution.NotOrchestrated);

        AllowedToolDto[] tools =
        [
            new()
            {
                Id = Guid.NewGuid(),
                Name = CoderToolDefinition.ReadFileToolName,
                Location = ToolLocation.ClientLocal,
                ParameterSchema = "{\"type\":\"object\"}",
                RequiresApproval = false,
                Category = ToolCategory.ReadLocal
            }
        ];
        var offer = Substitute.For<ILocalToolOfferProvider>();
        offer.GetOfferedTools(Arg.Any<string?>(), Arg.Any<bool>()).Returns(tools);
        offer.GetOfferedToolsAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(tools);
        offer.GetOfferedToolsForProfileAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(tools);

        var defaultAgent = Substitute.For<IDefaultAgentProvider>();
        defaultAgent.GetDefaultAgentIdAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<Guid?>(null));
        var settingsStore = Substitute.For<INodeSettingsStore>();
        settingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        var defaultModel = new LocalChatAgentOptions().DefaultModel;
        var modelResolver = Substitute.For<ILocalDefaultChatModelResolver>();
        modelResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                     .Returns(callInfo => Task.FromResult<string?>(string.IsNullOrWhiteSpace(callInfo.Arg<string?>()) ? defaultModel : callInfo.Arg<string?>()));

        return new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(agentResolver, agentStore, orchestration, CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
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
            offer,
            defaultAgent,
            settingsStore,
            modelResolver,
            Substitute.For<IMemoryExtractionDispatcher>(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            new ChatTurnContextBuilder(Substitute.For<IConversationUploadedFileStore>(),
                new UntrustedContentFenceSeedProvider(CreateFenceKeyHolder()),
                Substitute.For<IServiceScopeFactory>(),
                Options.Create(new LocalChatAgentOptions()),
                NullLogger<ChatTurnContextBuilder>.Instance),
            stager,
            Options.Create(new KnowledgeBaseOptions()),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance);
    }

    private static IModelCapabilityResolver CreateModelCapabilityResolver()
    {
        var classification = Substitute.For<IModelClassificationService>();
        classification.ClassifyAsync(Arg.Any<IEnumerable<ModelIdentity>>(), Arg.Any<CancellationToken>())
                      .Returns(callInfo =>
                      {
                          var map = new Dictionary<string, ModelClassificationResult>(StringComparer.OrdinalIgnoreCase);
                          foreach (var (modelName, _) in callInfo.Arg<IEnumerable<ModelIdentity>>())
                          {
                              if (!string.IsNullOrWhiteSpace(modelName))
                              {
                                  map[modelName] = new ModelClassificationResult
                                  {
                                      ModelName = modelName,
                                      Kind = ModelKind.Chat,
                                      DetectedKind = ModelKind.Chat,
                                      Capabilities = ["completion", "tools"],
                                      IsOverridden = false
                                  };
                              }
                          }

                          return Task.FromResult<IReadOnlyDictionary<string, ModelClassificationResult>>(map);
                      });
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns(OllamaLocalModelProvider.OllamaProviderName);
        var gguf = Substitute.For<IGgufModelCapabilityResolver>();
        gguf.TryResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((GgufModelCapabilities?)null);

        return new ModelCapabilityResolver(classification,
            providerResolver,
            gguf,
            Substitute.For<IActiveCloudChatClientFactory>(),
            new FakeModelTrustResolver(),
            NullLogger<ModelCapabilityResolver>.Instance);
    }

    private static INodeChatPersistenceService CreatePersistence(Guid conversationId, Guid assistantMessageId, Guid requestId)
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
            Messages = []
        };
        var userMessage = new NodeChatPersistedMessageDto
        {
            MessageId = Guid.NewGuid(),
            ConversationId = conversationId,
            RequestId = null,
            Sequence = 1,
            Role = "user",
            Content = "run a command",
            Reasoning = null,
            Status = NodeChatMessageStatusValues.Completed,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1,
            Model = null,
            Error = null,
            MetadataJson = null
        };
        var pending = AssistantMessage(conversationId, assistantMessageId, requestId, NodeChatMessageStatusValues.Pending, error: null);

        persistence.GetConversationAsync(conversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        persistence.GetConversationForTurnAsync(conversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        persistence.PersistUserMessageAsync(Arg.Any<NodeChatPersistUserMessageRequest>(), Arg.Any<CancellationToken>()).Returns(userMessage);
        persistence.CreateAssistantPlaceholderAsync(Arg.Any<NodeChatCreateAssistantPlaceholderRequest>(), Arg.Any<CancellationToken>()).Returns(pending);
        persistence.MarkAssistantQueuedAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                   .Returns(pending with
                   {
                       Status = NodeChatMessageStatusValues.Queued
                   });
        persistence.MarkAssistantStreamingAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                   .Returns(pending with
                   {
                       Status = NodeChatMessageStatusValues.Streaming
                   });
        persistence.TerminalizeAssistantMessageAsync(Arg.Any<NodeChatTerminalizeMessageRequest>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo =>
                   {
                       var request = callInfo.ArgAt<NodeChatTerminalizeMessageRequest>(0);
                       return AssistantMessage(conversationId, assistantMessageId, requestId, request.Status, request.Error);
                   });
        return persistence;
    }

    private static NodeChatPersistedMessageDto AssistantMessage(Guid conversationId, Guid assistantMessageId, Guid requestId, string status, string? error)
    {
        return new NodeChatPersistedMessageDto
        {
            MessageId = assistantMessageId,
            ConversationId = conversationId,
            RequestId = requestId,
            Sequence = 2,
            Role = "assistant",
            Content = string.Empty,
            Reasoning = null,
            Status = status,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1,
            Model = null,
            Error = error,
            MetadataJson = null
        };
    }

    private static INodeSqliteKeyHolder CreateFenceKeyHolder()
    {
        var holder = Substitute.For<INodeSqliteKeyHolder>();
        holder.Key.Returns(new ReadOnlyMemory<byte>(new byte[32]));
        return holder;
    }

    // Ends the run the way an unanswered approval does: the real runner classifies the sweep's ApprovalExpiredException and
    // reports the failure; the faulting variant covers a run task that escapes with the exception instead.
    private sealed class ExpiredApprovalInvocationRunner : IInvocationRunner
    {
        private readonly IWorkerEventDispatcher _dispatcher;
        private readonly bool _fault;

        public ExpiredApprovalInvocationRunner(IWorkerEventDispatcher dispatcher, bool fault)
        {
            _dispatcher = dispatcher;
            _fault = fault;
        }

        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            var expired = new ApprovalExpiredException("run_in_agent_home");
            if (_fault)
            {
                throw expired;
            }

            await _dispatcher.ReportInvocationFailedAsync(context.Package.InvocationId, expired.Message, FailureCategory.Timeout);
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
}
