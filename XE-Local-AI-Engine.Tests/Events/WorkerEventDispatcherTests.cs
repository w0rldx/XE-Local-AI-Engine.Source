namespace XE_Local_AI_Engine.Tests.Events;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

public sealed class WorkerEventDispatcherTests
{
    [Test]
    public void CurrentInvocation_Initially_IsNull()
    {
        var dispatcher = CreateDispatcher(Substitute.For<IInvocationRunner>());

        AssertEx.Null(dispatcher.CurrentInvocation);
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_CallsRunnerRunAsync()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);
        var package = RuntimePackageBuilder.Valid().Build();

        await dispatcher.DispatchInvocationAssignedAsync(package);

        await runner.Received(1).RunAsync(package, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_WhenAlreadyBusy_LogsAndDrops()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.RunAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>()).Returns(_ => gate.Task);

        var dispatcher = CreateDispatcher(runner);
        var first = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build();
        var second = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build();

        var firstDispatch = dispatcher.DispatchInvocationAssignedAsync(first);
        await Task.Delay(20);
        await dispatcher.DispatchInvocationAssignedAsync(second);

        await runner.Received(1).RunAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>());
        AssertEx.Equal(first.InvocationId, dispatcher.CurrentInvocation?.InvocationId ?? Guid.Empty);

        gate.SetResult();
        await firstDispatch;
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_SetsCurrentInvocation()
    {
        var dispatcher = CreateDispatcher(Substitute.For<IInvocationRunner>());
        var package = RuntimePackageBuilder.Valid().Build();

        await dispatcher.DispatchInvocationAssignedAsync(package);

        var current = AssertEx.NotNull(dispatcher.CurrentInvocation);
        AssertEx.Equal(package.InvocationId, current.InvocationId);
        AssertEx.Equal(package.ConversationId, current.ConversationId);
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_RaisesInvocationStateChangedEvent()
    {
        var dispatcher = CreateDispatcher(Substitute.For<IInvocationRunner>());
        var package = RuntimePackageBuilder.Valid().Build();
        var eventCount = 0;
        dispatcher.InvocationStateChanged += (_, _) => eventCount++;

        await dispatcher.DispatchInvocationAssignedAsync(package);

        AssertEx.True(eventCount >= 2);
    }

    [Test]
    public async Task DispatchToolCallResultAsync_CallsResolveToolCallResult()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);
        var evt = new ToolCallResultEvent
        {
            RequestId = "req-1",
            Result = "ok"
        };

        await dispatcher.DispatchToolCallResultAsync(evt);

        runner.Received(1).ResolveToolCallResult(evt);
    }

    [Test]
    public async Task DispatchApprovalResolvedAsync_OnlyLogs_DoesNotCallRunner()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);

        await dispatcher.DispatchApprovalResolvedAsync(new ApprovalResolvedEvent
        {
            RequestId = "req-1",
            Approved = true
        });

        runner.DidNotReceive().Cancel(Arg.Any<Guid>());
        runner.DidNotReceive().CancelAll();
        runner.DidNotReceive().ResolveToolCallResult(Arg.Any<ToolCallResultEvent>());
        await runner.DidNotReceive().RunAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DispatchInvocationCancelledAsync_CallsCancelOnRunner()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);
        var package = RuntimePackageBuilder.Valid().Build();
        await dispatcher.DispatchInvocationAssignedAsync(package);

        await dispatcher.DispatchInvocationCancelledAsync(new InvocationCancelledEvent
        {
            InvocationId = package.InvocationId,
            Reason = "cancelled"
        });

        runner.Received(1).Cancel(package.InvocationId);
    }

    [Test]
    public async Task DispatchDisconnectRequestedAsync_CallsCancelAllOnRunner()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);

        await dispatcher.DispatchDisconnectRequestedAsync(new DisconnectRequestedEvent
        {
            Reason = "shutdown"
        });

        runner.Received(1).CancelAll();
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_WhenRunnerThrows_MarksInvocationFailed()
    {
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromException(new InvalidOperationException("boom")));

        var dispatcher = CreateDispatcher(runner);
        var package = RuntimePackageBuilder.Valid().Build();

        await dispatcher.DispatchInvocationAssignedAsync(package);

        var current = AssertEx.NotNull(dispatcher.CurrentInvocation);
        AssertEx.Equal(package.InvocationId, current.InvocationId);
        AssertEx.Equal(InvocationStatus.Failed, current.Status);
        AssertEx.Equal("boom", current.Error);
    }

    private static WorkerEventDispatcher CreateDispatcher(IInvocationRunner runner)
    {
        return new WorkerEventDispatcher(runner, NullLogger<WorkerEventDispatcher>.Instance);
    }
}
