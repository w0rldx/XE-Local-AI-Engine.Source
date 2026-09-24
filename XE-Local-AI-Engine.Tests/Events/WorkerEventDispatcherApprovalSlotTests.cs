namespace XE_Local_AI_Engine.Tests.Events;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The pending-approval slot a reconnect replays from, fed through the real <see cref="WorkerEventDispatcher" />
///     exactly as the approval coordinator feeds it: the platform-contract request first, then the lifecycle payload.
/// </summary>
/// <remarks>
///     <c>InvocationResumeRegistryTests</c> replays from a HAND-BUILT slot that already carries the tool name, so it
///     could not see that the dispatcher never wrote one; a reload then replayed the approval with no tool name.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class WorkerEventDispatcherApprovalSlotTests
{
    [Test]
    public async Task ApprovalLifecycle_FoldsCallIdToolNameAndArgumentsOntoTheSlot_AndTheReplayCarriesThem()
    {
        var dispatcher = new WorkerEventDispatcher(Substitute.For<IInvocationRunner>(),
            Substitute.For<IInvocationHistory>(),
            NullLogger<WorkerEventDispatcher>.Instance,
            TimeProvider.System);
        var registry = new InvocationResumeRegistry(dispatcher, TimeProvider.System, NullLogger<InvocationResumeRegistry>.Instance);
        var invocationId = Guid.NewGuid();
        var package = RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithConversationId(Guid.NewGuid()).Build();
        const string arguments = "{\"command\":\"echo hello\"}";

        await using var lease = await dispatcher.ReportInvocationAssignedAsync(package);
        await dispatcher.ReportApprovalRequestedAsync(new ApprovalRequestPayload
        {
            InvocationId = invocationId,
            RequestId = "approval-1",
            Description = "A tool call requires approval."
        });
        await dispatcher.ReportApprovalLifecycleAsync(new ApprovalLifecyclePayload
        {
            InvocationId = invocationId,
            RequestId = "approval-1",
            CallId = "call-1",
            ToolName = "run_in_agent_home",
            Description = "A tool call requires approval.",
            Arguments = arguments,
            SessionScopeEligible = false
        });

        var slot = AssertEx.NotNull(dispatcher.CurrentInvocation?.PendingApproval);
        AssertEx.Equal("call-1", slot.CallId);
        AssertEx.Equal("run_in_agent_home", slot.ToolName);
        AssertEx.Equal(arguments, slot.Arguments);
        AssertEx.Equal(expected: false, slot.SessionScopeEligible);

        var events = new List<ChatStreamEvent>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var consumer = Task.Run(async () =>
        {
            await foreach (var streamEvent in registry.ResumeAsync(invocationId, cancellation.Token))
            {
                events.Add(streamEvent);
            }
        }, cancellation.Token);

        await AssertEx.EventuallyAsync(() => events.Any(evt => evt.Type == ChatStreamEventTypes.ApprovalRequested), TimeSpan.FromSeconds(10));
        await dispatcher.ReportInvocationCompletedAsync(invocationId);
        await consumer;

        var replayed = events.Single(evt => evt.Type == ChatStreamEventTypes.ApprovalRequested);
        AssertEx.Equal("approval-1", replayed.ApprovalRequestId);
        AssertEx.Equal("call-1", replayed.ToolCallId);
        AssertEx.Equal("run_in_agent_home", replayed.ToolName);
        AssertEx.Equal(arguments, replayed.Arguments);
    }
}
