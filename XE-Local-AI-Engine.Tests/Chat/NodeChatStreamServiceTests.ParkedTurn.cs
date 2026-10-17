namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

// Helpers for the R4-03 parked-turn tests: the real dispatcher slot and a runner that parks one conversation.
public sealed partial class NodeChatStreamServiceTests
{
    private static WorkerEventDispatcher CreateRealDispatcher()
    {
        return new WorkerEventDispatcher(Substitute.For<IInvocationRunner>(),
            Substitute.For<IInvocationHistory>(),
            NullLogger<WorkerEventDispatcher>.Instance,
            TimeProvider.System);
    }

    private static RuntimePackage RuntimePackageBuilderFor(Guid conversationId)
    {
        return RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).WithConversationId(conversationId).Build();
    }

    private static async Task<List<ChatStreamEvent>> DrainAsync(IAsyncEnumerable<ChatStreamEvent> stream)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in stream)
        {
            events.Add(streamEvent);
        }

        return events;
    }

    private static NodeChatStreamService CreateParkingService(INodeChatPersistenceService persistence, IInvocationRunner runner, IWorkerEventDispatcher dispatcher)
    {
        return new NodeChatStreamService(persistence,
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
    }

    /// <summary>Parks the turn of one conversation on an approval through a real <see cref="HumanPark" />; every other turn completes at once.</summary>
    private sealed class ParkingInvocationRunner : IInvocationRunner
    {
        private readonly WorkerEventDispatcher _dispatcher;
        private readonly Guid _parkedConversationId;

        private readonly InvocationLifecycleTracker _tracker = new(Substitute.For<IInvocationAttachmentTracker>(),
            new PendingToolCallRegistry(),
            StubNodeRuntimeSettings.Create().Build(),
            TimeProvider.System);

        private readonly bool _holdOthers;

        public ParkingInvocationRunner(WorkerEventDispatcher dispatcher, Guid parkedConversationId, bool holdOthers = false)
        {
            _dispatcher = dispatcher;
            _parkedConversationId = parkedConversationId;
            _holdOthers = holdOthers;
        }

        /// <summary>Set once another conversation's turn holds the slot, when the runner was built to hold it there.</summary>
        public TaskCompletionSource OtherRunning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseOther { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Parked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            var invocationId = context.Package.InvocationId;
            if (context.Package.ConversationId == _parkedConversationId)
            {
                _tracker.RegisterActiveInvocation(invocationId, TimeSpan.FromMinutes(5), cancellationToken);
                try
                {
                    await _dispatcher.ReportApprovalRequestedAsync(new ApprovalRequestPayload
                    {
                        InvocationId = invocationId,
                        RequestId = "approval-parked",
                        Description = "A tool call requires approval."
                    });
                    var park = new HumanPark(_tracker, _dispatcher, invocationId);
                    park.Park();
                    Parked.SetResult();
                    await Answer.Task.WaitAsync(cancellationToken);
                    await _dispatcher.DispatchApprovalResolvedAsync(new ApprovalResolvedEvent
                    {
                        RequestId = "approval-parked",
                        Approved = true
                    });
                    await park.ResumeAsync(cancellationToken);
                }
                finally
                {
                    _tracker.ClearActiveInvocation(invocationId);
                }
            }
            else if (_holdOthers)
            {
                OtherRunning.TrySetResult();
                await ReleaseOther.Task.WaitAsync(cancellationToken);
            }

            await _dispatcher.ReportInvocationStreamChunkAsync(invocationId, "answer");
            await _dispatcher.ReportInvocationCompletedAsync(invocationId, inputTokens: 10, outputTokens: 3, totalTokens: 13);
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
}
