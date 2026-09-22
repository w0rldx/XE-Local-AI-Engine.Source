namespace XE_Local_AI_Engine.Tests.Hosting;

using System.IO.Pipes;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class DesktopParentLifetimeTests
{
    [Test]
    public void ResolvePipeName_StandaloneAndNonDesktopModesDoNotEnableMonitoring()
    {
        AssertEx.Null(DesktopParentLifetime.ResolvePipeName(LaunchMode.Desktop, null));
        AssertEx.Null(DesktopParentLifetime.ResolvePipeName(LaunchMode.Headless, "invalid/path"));
        AssertEx.Null(DesktopParentLifetime.ResolvePipeName(LaunchMode.McpOnly, "invalid/path"));
        AssertEx.Equal("xe-desktop-abc123", DesktopParentLifetime.ResolvePipeName(LaunchMode.Desktop, "xe-desktop-abc123"));
    }

    [Test]
    [Arguments("")]
    [Arguments("../other")]
    [Arguments("pipe\\name")]
    [Arguments("pipe name")]
    [Arguments("pipe\nname")]
    [Arguments("pipé")]
    public void ResolvePipeName_InvalidDesktopConfigurationFails(string name)
    {
        AssertEx.Throws<ArgumentException>(() => DesktopParentLifetime.ResolvePipeName(LaunchMode.Desktop, name));
    }

    [Test]
    public void ResolvePipeName_RejectsOversizedNames()
    {
        AssertEx.Throws<ArgumentException>(() => DesktopParentLifetime.ResolvePipeName(LaunchMode.Desktop, new string('a', 129)));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ParentLossDuringBootstrap_TerminatesWithinBoundedGrace(bool sendInput)
    {
        var name = NewPipeName();
        using var server = CreateServer(name);
        var clock = new ManualTimeProvider();
        var terminated = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new DesktopParentLifetime(name, clock, code => terminated.TrySetResult(code));
        // real-timer: bounds OS pipe I/O; the shutdown grace uses the manual clock.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connected = server.WaitForConnectionAsync(deadline.Token);
        await monitor.StartAsync(deadline.Token);
        await connected;
        if (sendInput)
        {
            await server.WriteAsync(new byte[1], deadline.Token);
        }
        else
        {
            server.Disconnect();
        }

        await monitor.ParentLost.WaitAsync(deadline.Token);
        clock.Advance(DesktopParentLifetime.ShutdownGrace - TimeSpan.FromSeconds(1));
        AssertEx.False(terminated.Task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        AssertEx.Equal(1, await terminated.Task.WaitAsync(deadline.Token));
        await monitor.Completion.WaitAsync(deadline.Token);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ParentLoss_RequestsGracefulStopWhetherHostBindsBeforeOrAfterLoss(bool bindBeforeLoss)
    {
        var name = NewPipeName();
        using var server = CreateServer(name);
        var clock = new ManualTimeProvider();
        var terminated = false;
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime.When(static value => value.StopApplication()).Do(_ => stopped.TrySetResult());
        await using var monitor = new DesktopParentLifetime(name, clock, _ => terminated = true);
        // real-timer: bounds OS pipe I/O and completion gates, not the behavior's timer.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connected = server.WaitForConnectionAsync(deadline.Token);
        await monitor.StartAsync(deadline.Token);
        await connected;
        if (bindBeforeLoss)
        {
            monitor.Bind(lifetime);
        }

        server.Disconnect();
        await monitor.ParentLost.WaitAsync(deadline.Token);
        if (!bindBeforeLoss)
        {
            monitor.Bind(lifetime);
        }

        await stopped.Task.WaitAsync(deadline.Token);
        lifetime.Received(1).StopApplication();
        await monitor.DisposeAsync();
        clock.Advance(DesktopParentLifetime.ShutdownGrace);
        await monitor.Completion.WaitAsync(deadline.Token);
        AssertEx.True(terminated);
        AssertEx.Equal(0, clock.ArmedTimerCount);
    }

    [Test]
    public async Task BlockedGracefulStopAndDisposal_CannotDisarmParentLossDeadline()
    {
        var name = NewPipeName();
        using var server = CreateServer(name);
        using var releaseStop = new ManualResetEventSlim();
        var clock = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminated = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        // real-timer: bounds real pipe I/O and the deliberately blocked synchronous shutdown callback.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        lifetime.When(static value => value.StopApplication()).Do(_ =>
        {
            entered.TrySetResult();
#pragma warning disable MA0045 // Deliberately simulate a synchronous StopApplication callback that cannot finish until the test releases it.
            releaseStop.Wait(deadline.Token);
#pragma warning restore MA0045
        });
        await using var monitor = new DesktopParentLifetime(name, clock, code => terminated.TrySetResult(code));
        try
        {
            var connected = server.WaitForConnectionAsync(deadline.Token);
            await monitor.StartAsync(deadline.Token);
            await connected;
            monitor.Bind(lifetime);
            server.Disconnect();
            await entered.Task.WaitAsync(deadline.Token);
            await monitor.DisposeAsync().AsTask().WaitAsync(deadline.Token);
            clock.Advance(DesktopParentLifetime.ShutdownGrace);
            AssertEx.Equal(1, await terminated.Task.WaitAsync(deadline.Token));
        }
        finally
        {
            releaseStop.Set();
        }

        await monitor.Completion.WaitAsync(deadline.Token);
        lifetime.Received(1).StopApplication();
    }

    [Test]
    public async Task NormalHostStopping_CancelsReadWithoutParentLossOrTermination()
    {
        var name = NewPipeName();
        using var server = CreateServer(name);
        using var stopping = new CancellationTokenSource();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(stopping.Token);
        var clock = new ManualTimeProvider();
        var terminated = false;
        await using var monitor = new DesktopParentLifetime(name, clock, _ => terminated = true);
        // real-timer: bounds OS pipe I/O and completion gates.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connected = server.WaitForConnectionAsync(deadline.Token);
        await monitor.StartAsync(deadline.Token);
        await connected;
        monitor.Bind(lifetime);
        await stopping.CancelAsync();
        await monitor.Completion.WaitAsync(deadline.Token);
        clock.Advance(DesktopParentLifetime.ShutdownGrace);
        AssertEx.False(terminated);
        AssertEx.False(monitor.ParentLost.IsCompleted);
        lifetime.DidNotReceive().StopApplication();
    }

    [Test]
    public async Task BootstrapFailure_DisposalClosesPipeWithoutTermination()
    {
        var name = NewPipeName();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var clock = new ManualTimeProvider();
        var terminated = false;
        await using var monitor = new DesktopParentLifetime(name, clock, _ => terminated = true);
        // real-timer: bounds actual OS pipe connection and EOF observation.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connected = server.WaitForConnectionAsync(deadline.Token);
        await monitor.StartAsync(deadline.Token);
        await connected;
        await monitor.DisposeAsync();
        AssertEx.Equal(0, await server.ReadAsync(new byte[1], deadline.Token));
        clock.Advance(DesktopParentLifetime.ShutdownGrace);
        AssertEx.False(terminated);
    }

    [Test]
    public async Task MissingParent_FailsStartupInsteadOfBootstrappingWithoutOwner()
    {
        var terminated = false;
        await using var monitor = new DesktopParentLifetime(NewPipeName(), new ManualTimeProvider(),
            _ => terminated = true, connectTimeoutMilliseconds: 0);
        await AssertEx.ThrowsAsync<TimeoutException>(() => monitor.StartAsync(CancellationToken.None));
        AssertEx.True(monitor.Completion.IsCompleted);
        AssertEx.False(terminated);
    }

    [Test]
    public async Task CanceledConnection_DoesNotStartMonitor()
    {
        var terminated = false;
        await using var monitor = new DesktopParentLifetime(NewPipeName(), new ManualTimeProvider(), _ => terminated = true);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => monitor.StartAsync(cancellation.Token));
        AssertEx.True(monitor.Completion.IsCompleted);
        AssertEx.False(terminated);
    }

    private static string NewPipeName() => $"xe-desktop-{Guid.NewGuid():N}";

    private static NamedPipeServerStream CreateServer(string name) =>
        new(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}
