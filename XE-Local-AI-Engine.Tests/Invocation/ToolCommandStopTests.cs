namespace XE_Local_AI_Engine.Tests.Invocation;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>A chat tool command's operator stop: findable from the moment it is announced, and marked only by a stop that worked.</summary>
[Category(TestCategories.Unit)]
public sealed class ToolCommandStopTests
{
    private const string CallId = "call-1";
    private const string ExecutionId = "exec-1";

    [Test]
    public async Task AStopSentOnTheRunningAnnouncement_FindsTheCommand()
    {
        var messageId = Guid.NewGuid();
        var stops = new ToolCommandStopRegistry();
        var provider = CancellableProvider();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        bool? stoppedFromAnnouncement = null;
        dispatcher.When(static target => target.ReportToolCallOutput(Arg.Is<ToolCallOutputPayload>(static payload => payload.CommandRunning)))
                  .Do(_ => stoppedFromAnnouncement ??= stops.TryStopAsync(messageId, CallId, CancellationToken.None).GetAwaiter().GetResult());

        using (ToolCommandOutputScope.Open(Guid.NewGuid(), messageId, dispatcher, stops, TimeProvider.System, NullLogger.Instance))
            using (AmbientToolCall.Enter(CallId, "run_command"))
                using (ToolCommandOutputScope.BindCurrentCall())
                {
                    using var output = AssertEx.NotNull(ToolCommandOutputScope.StartCommand(provider, Handle(), ExecutionId, workspaceRoot: null, "sleep 300"));
                    AssertEx.True(output.StopRequested, "the stop sent on the announcement marks the command");
                }

        AssertEx.True(stoppedFromAnnouncement == true, "the command was not registered yet when the operator saw it running");
        await provider.Received(1).CancelCommandAsync(Arg.Any<SandboxHandle>(), ExecutionId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AStopThatThrows_LeavesTheCommandUnmarkedAndRethrows()
    {
        var messageId = Guid.NewGuid();
        var stops = new ToolCommandStopRegistry();
        var provider = CancellableProvider();
        provider.CancelCommandAsync(Arg.Any<SandboxHandle>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new InvalidOperationException("daemon gone")));
        using var registration = AssertEx.NotNull(stops.Register(messageId, CallId, provider, Handle(), ExecutionId));

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => stops.TryStopAsync(messageId, CallId, CancellationToken.None));

        AssertEx.False(registration.StopRequested, "a stop that failed must not make a timeout read as an operator stop");
    }

    // Guards the reset against a naive reset-in-catch, which would clear the mark the concurrent successful stop set.
    [Test]
    public async Task TwoConcurrentStops_WhereOneFails_KeepTheSuccessfulStopMarked()
    {
        var messageId = Guid.NewGuid();
        var stops = new ToolCommandStopRegistry();
        var provider = CancellableProvider();
        var failing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.CancelCommandAsync(Arg.Any<SandboxHandle>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(failing.Task, Task.CompletedTask);
        using var registration = AssertEx.NotNull(stops.Register(messageId, CallId, provider, Handle(), ExecutionId));

        var first = stops.TryStopAsync(messageId, CallId, CancellationToken.None);
        AssertEx.True(await stops.TryStopAsync(messageId, CallId, CancellationToken.None), "the second stop reached the provider");
        failing.SetException(new InvalidOperationException("daemon gone"));
        await AssertEx.ThrowsAsync<InvalidOperationException>(() => first);

        AssertEx.True(registration.StopRequested, "the failed stop cleared the mark the successful one set");
    }

    private static ISandboxRuntimeProvider CancellableProvider()
    {
        var provider = Substitute.For<ISandboxRuntimeProvider>();
        provider.Capabilities.Returns(SandboxProviderCapabilities.SupportsCommandCancellation);
        return provider;
    }

    private static SandboxHandle Handle() => new()
    {
        ProviderName = "fake",
        SandboxId = "sandbox-1",
        AttachKey = new SandboxAttachKey
        {
            OwnerUserId = "owner-1",
            NodeId = "node-1",
            ProviderName = "fake",
            RuntimeProfile = "default",
            ManifestVersion = 1
        },
        CreatedAt = DateTimeOffset.UnixEpoch,
        ManifestVersion = 1
    };
}
