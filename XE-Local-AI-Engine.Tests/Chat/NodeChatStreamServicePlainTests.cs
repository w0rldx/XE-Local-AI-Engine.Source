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
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using static NodeChatStreamServiceTestSupport;

/// <summary>
///     A plain turn: no agent, tools, skills, knowledge base or memory hook; its custom prompt verbatim or none.
/// </summary>
/// <remarks>Attachments still travel, and the non-plain Default Assistant path is held unchanged beside it.</remarks>
[Category(TestCategories.Unit)]
public sealed class NodeChatStreamServicePlainTests
{
    private const string PersonaPrompt = "Default Assistant persona.";

    [Test]
    public async Task SendMessageAsync_WhenPlainWithoutPrompt_OmitsSystemPromptAndInjectsNothing()
    {
        var harness = new Harness(isPlain: true, plainSystemPrompt: null);

        await harness.SendAsync(new NodeChatStreamRequest(harness.ConversationId,
            "hello",
            MessageId: harness.AssistantMessageId,
            RequestId: harness.RequestId,
            UseLocalTools: true,
            AttachmentFileIds: [Guid.NewGuid()],
            UseKnowledgeBase: true));

        var package = AssertEx.NotNull(harness.Runner.LastPackage, "The plain turn must still run.");
        AssertEx.True(package.OmitSystemPrompt, "A plain turn without a custom prompt sends no system message.");
        AssertEx.Equal(string.Empty, package.ResolvedSystemPrompt);
        AssertEx.Empty(package.AllowedTools, "A plain turn offers no tools, whatever the composer toggle says.");
        AssertEx.Null(package.Skills);
        AssertEx.Null(package.CustomTools);
        AssertEx.Null(package.OrchestrationSpec);

        // No agent resolution at all: the Default Assistant is never looked up and the resolver never consulted.
        await harness.DefaultAgentProvider.DidNotReceive().GetDefaultAgentIdAsync(Arg.Any<CancellationToken>());
        await harness.AgentResolver.DidNotReceiveWithAnyArgs().ResolveAsync(default(Guid?), default, default, default, default, default, default);
        var placeholder = AssertEx.NotNull(harness.Placeholder);
        AssertEx.Null(placeholder.AgentDefinitionId);
        AssertEx.Null(placeholder.AgentName);

        // The knowledge base is ignored; the attachments still travel.
        await harness.TurnContextBuilder.DidNotReceiveWithAnyArgs().BuildKnowledgeContextAsync(default!, default!, default, default);
        await harness.TurnContextBuilder.DidNotReceiveWithAnyArgs().IsKnowledgeEmbeddingAvailableAsync(default);
        await harness.TurnContextBuilder.Received(1).BuildAttachmentContextAsync(harness.ConversationId, Arg.Any<IReadOnlyList<Guid>?>(), Arg.Any<CancellationToken>());

        // The memory agent the default id would have resolved to never gets an extraction hook.
        harness.ExtractionDispatcher.DidNotReceiveWithAnyArgs().Dispatch(default!, default!);
    }

    [Test]
    public async Task SendMessageAsync_WhenPlainWithPrompt_SendsThePromptVerbatim()
    {
        const string prompt = "Answer in one sentence.";
        var harness = new Harness(isPlain: true, plainSystemPrompt: prompt);

        await harness.SendAsync(new NodeChatStreamRequest(harness.ConversationId, "hello", MessageId: harness.AssistantMessageId, RequestId: harness.RequestId));

        var package = AssertEx.NotNull(harness.Runner.LastPackage);
        AssertEx.False(package.OmitSystemPrompt, "A custom prompt is sent.");
        AssertEx.Equal(prompt, package.ResolvedSystemPrompt);
        AssertEx.Empty(package.AllowedTools);
    }

    [Test]
    public async Task SendMessageAsync_WhenPlainAndRequestPicksAgent_RejectsBeforePersisting()
    {
        var harness = new Harness(isPlain: true, plainSystemPrompt: null);

        await AssertEx.ThrowsAsync<NodeChatInvalidRequestException>(() => harness.SendAsync(new NodeChatStreamRequest(harness.ConversationId,
            "hello",
            MessageId: harness.AssistantMessageId,
            RequestId: harness.RequestId,
            AgentDefinitionId: Guid.NewGuid())));

        await harness.Persistence.DidNotReceiveWithAnyArgs().PersistUserMessageAsync(default!, default);
        AssertEx.Null(harness.Runner.LastPackage, "A refused send never runs.");
    }

    [Test]
    public async Task SendMessageAsync_WhenNotPlain_KeepsTheDefaultAssistantPath()
    {
        var harness = new Harness(isPlain: false, plainSystemPrompt: "Stored but inactive.");

        await harness.SendAsync(new NodeChatStreamRequest(harness.ConversationId,
            "hello",
            MessageId: harness.AssistantMessageId,
            RequestId: harness.RequestId,
            UseLocalTools: true));

        var package = AssertEx.NotNull(harness.Runner.LastPackage);
        AssertEx.False(package.OmitSystemPrompt, "The Default Assistant path sends its persona.");
        AssertEx.Equal(PersonaPrompt, package.ResolvedSystemPrompt);
        AssertEx.Equal(expected: 1, package.AllowedTools.Count);
        await harness.AgentResolver.Received(1)
                     .ResolveAsync(harness.DefaultAgentId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        AssertEx.Equal(harness.DefaultAgentId, AssertEx.NotNull(harness.Placeholder).AgentDefinitionId);
        harness.ExtractionDispatcher.ReceivedWithAnyArgs(1).Dispatch(default!, default!);
    }

    /// <summary>The pre-send estimate must measure the request a plain send really makes: same prompt, same empty offer.</summary>
    [Test]
    [Arguments(null)]
    [Arguments("Answer in one sentence.")]
    public async Task SendMessageAsync_WhenPlain_ResolvesTheSamePromptAndToolOfferAsTheContextEstimate(string? prompt)
    {
        const string model = "parity-model";
        var harness = new Harness(isPlain: true, prompt);
        var estimator = new HeuristicTokenEstimator();
        var estimateService = harness.CreateEstimateService(model, estimator);

        await harness.SendAsync(new NodeChatStreamRequest(harness.ConversationId,
            "hello",
            MessageId: harness.AssistantMessageId,
            RequestId: harness.RequestId,
            Model: model,
            UseLocalTools: true));

        var estimate = AssertEx.NotNull(await estimateService.EstimateAsync(new ChatContextEstimateRequest
        {
            ConversationId = harness.ConversationId,
            ModelName = model
        }));
        var inputs = await estimateService.ResolveAsync(model, agentId: null, useLocalTools: true, CancellationToken.None, new NodeChatConversationPlainMode
        {
            IsPlain = true,
            PlainSystemPrompt = prompt
        });

        var package = AssertEx.NotNull(harness.Runner.LastPackage);
        AssertEx.Equal(package.ResolvedSystemPrompt, inputs.SystemPrompt);
        AssertEx.Empty(package.AllowedTools);
        AssertEx.Empty(inputs.Offer.AllowedTools ?? []);
        AssertEx.Empty(estimate.Tools);
        AssertEx.Equal(package.OmitSystemPrompt
                ? 0
                : ConversationContextBudgeter.EstimateSystemPromptTokens(estimator, package.ResolvedSystemPrompt, estimator.ResolveDivisor(model)),
            AssertEx.NotNull(estimate.Estimated).SystemPromptTokens);
    }

    internal sealed class Harness
    {
        private readonly ChatTurnResolver _turnResolver;
        private readonly NodeChatStreamService _service;

        public Harness(bool isPlain, string? plainSystemPrompt)
            : this(Guid.NewGuid(), persistence: null)
        {
            var conversation = new NodeChatConversationDto
            {
                ConversationId = ConversationId,
                Title = "plain",
                UserId = null,
                CreatedAtUtc = 1,
                LastSeenUtc = 1,
                Purged = false,
                Messages = [],
                IsPlain = isPlain,
                PlainSystemPrompt = plainSystemPrompt
            };
            Persistence.GetConversationForTurnAsync(ConversationId, Arg.Any<CancellationToken>()).Returns(conversation);
            Persistence.GetConversationPlainModeAsync(ConversationId, Arg.Any<CancellationToken>()).Returns(new NodeChatConversationPlainMode
            {
                IsPlain = isPlain,
                PlainSystemPrompt = plainSystemPrompt
            });
        }

        /// <summary>A turn into <paramref name="conversationId" /> over the given persistence, or a substitute that stores nothing when null.</summary>
        public Harness(Guid conversationId, INodeChatPersistenceService? persistence)
        {
            ConversationId = conversationId;
            Persistence = persistence ?? CreatePersistence(ConversationId, AssistantMessageId, RequestId, _ => { }, placeholderObserver: request => Placeholder = request);
            DefaultAgentProvider = CreateDefaultAgentProvider(DefaultAgentId);
            AgentResolver = Substitute.For<IAgentDefinitionResolver>();
            AgentResolver.ResolveAsync(DefaultAgentId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                         .Returns(new ResolvedAgentRuntime(PersonaPrompt, [CreateLocalToolDto("Calculate", "{\"type\":\"object\"}")], ModelProfile: null, ReasoningEffort: null,
                             AgentDefinitionVersion: 2, DefaultAgentId, "Default Assistant", PlaybookEnabled: true, MemoryExtractionEnabled: true));

            TurnContextBuilder = Substitute.For<IChatTurnContextBuilder>();
            TurnContextBuilder.ListForeignAttachmentIdsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<Guid>?>(), Arg.Any<CancellationToken>()).Returns([]);
            TurnContextBuilder.ListUnsentAttachmentNamesAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<Guid>?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                              .Returns([]);
            TurnContextBuilder.ListTruncatedAttachmentNamesAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<Guid>?>(), Arg.Any<CancellationToken>()).Returns([]);

            var dispatcher = new RecordingWorkerEventDispatcher();
            Runner = new PackageCapturingInvocationRunner(dispatcher);
            _turnResolver = new ChatTurnResolver(AgentResolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance);
            _service = new NodeChatStreamService(Persistence,
                new ChatInvocationStatePump(ChatPumpTestFactory.Create(Persistence), TimeProvider.System),
                _turnResolver,
                new NodeChatMutationGuard(Persistence),
                new LocalChatRuntimePackageBuilder(),
                Runner,
                dispatcher,
                Options.Create(new LocalChatAgentOptions
                {
                    EnableTools = true
                }),
                StubNodeRuntimeSettings.Create().WithEnableTools(true).Build(),
                new NodeChatStreamCancellationRegistry(),
                CreateOfferProvider(CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}")),
                DefaultAgentProvider,
                CreateNodeSettingsStore(),
                CreateLocalDefaultChatModelResolver(),
                ExtractionDispatcher,
                Substitute.For<IConversationMaintenanceDispatcher>(),
                TurnContextBuilder,
                Substitute.For<IConversationSandboxStager>(),
                Options.Create(new ChatStreamBudgetOptions()),
                TimeProvider.System,
                new PermissiveToolApprovalPolicy(),
                Substitute.For<IGraphWorkflowStore>(),
                NullLogger<NodeChatStreamService>.Instance,
                new GpuWorkGate());
        }

        public Guid ConversationId { get; }

        public Guid AssistantMessageId { get; } = Guid.NewGuid();

        public Guid RequestId { get; } = Guid.NewGuid();

        public Guid DefaultAgentId { get; } = Guid.NewGuid();

        public INodeChatPersistenceService Persistence { get; }

        public IDefaultAgentProvider DefaultAgentProvider { get; }

        public IAgentDefinitionResolver AgentResolver { get; }

        public IChatTurnContextBuilder TurnContextBuilder { get; }

        public IMemoryExtractionDispatcher ExtractionDispatcher { get; } = Substitute.For<IMemoryExtractionDispatcher>();

        public PackageCapturingInvocationRunner Runner { get; }

        public NodeChatCreateAssistantPlaceholderRequest? Placeholder { get; private set; }

        public ChatContextEstimateService CreateEstimateService(string model, HeuristicTokenEstimator estimator)
        {
            var details = Substitute.For<ILocalModelDetailsResolver>();
            details.ResolveAsync(model, Arg.Any<CancellationToken>())
                   .Returns(new LocalModelDetailsResolution.Gguf(new LocalModelDescriptor
                   {
                       ModelName = model,
                       ProviderName = LlamaServerProviderConstants.ProviderName,
                       IsAvailable = true,
                       SizeBytes = null,
                       ModifiedAt = null,
                       MaxContextTokens = null
                   }, EffectiveContextTokens: 32768));
            return new ChatContextEstimateService(_turnResolver,
                DefaultAgentProvider,
                CreateNodeSettingsStore(),
                CreateLocalDefaultChatModelResolver(),
                StubNodeRuntimeSettings.Create().WithEnableTools(true).Build(),
                CreateOfferProvider(CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}")),
                new PermissiveToolApprovalPolicy(),
                Options.Create(new LocalChatAgentOptions
                {
                    EnableTools = true
                }),
                details,
                estimator,
                Options.Create(new ConversationContextBudgetOptions()),
                Persistence);
        }

        public async Task SendAsync(NodeChatStreamRequest request)
        {
            var drained = 0;
            await foreach (var _ in _service.SendMessageAsync(request))
            {
                drained++;
            }

            AssertEx.True(drained > 0, "Expected the send to stream events.");
        }
    }
}
