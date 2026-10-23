namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

// Helpers for the host-stop pump tests in NodeChatStreamServiceTests.cs: a disposed pump and the service wired to a lifetime.
public sealed partial class NodeChatStreamServiceTests
{
    private const string PumpFaultedFragment = "Local node chat stream pump faulted";

    private static async Task DrainWithDisposedPumpAsync(IHostApplicationLifetime lifetime, ILogger<NodeChatStreamService> logger)
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, static _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(new DisposedInvocationPump(), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            new CompletingInvocationRunner(dispatcher),
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
            logger,
            new GpuWorkGate(),
            lifetime);

        // The stream ends once the pump gives up; the drain in its finally is what logs.
        var events = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                               "hello",
                               MessageId: assistantMessageId,
                               RequestId: requestId),
                           CancellationToken.None))
        {
            events++;
        }

        AssertEx.True(events > 0, "The turn streamed before the pump gave up.");
    }

    // Every write lands on a disposed scope, as it does once the host has torn its service provider down.
    private sealed class DisposedInvocationPump : INodeChatInvocationPump
    {
        public Task<NodeChatPumpFlushResult> FlushDeltaAsync(NodeChatMessageCorrelation correlation,
            InvocationState state,
            NodeChatPumpCursor cursor,
            CancellationToken cancellationToken = default) =>
            throw new ObjectDisposedException("IServiceProvider");

        public Task<NodeChatPumpTerminalResult> TerminalizeAsync(NodeChatMessageCorrelation correlation,
            InvocationState state,
            string? requestedModel,
            IReadOnlyList<NodeChatMessagePart>? parts = null,
            IReadOnlyList<NodeChatMessageSource>? sources = null,
            ExecutionParent? parent = null) =>
            throw new ObjectDisposedException("IServiceProvider");

        public Task<NodeChatPumpTerminalResult> TerminalizeInterruptedAsync(NodeChatMessageCorrelation correlation,
            NodeChatPumpCursor cursor,
            bool wasCancelled,
            string? requestedModel = null) =>
            throw new ObjectDisposedException("IServiceProvider");
    }
}
