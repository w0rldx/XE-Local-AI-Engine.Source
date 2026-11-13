namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The fire-and-forget residency tick every runtime supervisor arms when it registers a child.</summary>
[Category(TestCategories.Unit)]
public sealed class ResidencyExitNotificationTests
{
    [Test]
    public async Task TickOnExitAsync_ChildExits_TicksOnce()
    {
        var notifier = new RecordingResidencyChangeNotifier();

        await ResidencyExitNotification.TickOnExitAsync(Task.FromResult(true), notifier, NullLogger.Instance, "test-server");

        AssertEx.Equal(expected: 1, notifier.Count);
    }

    [Test]
    public async Task TickOnExitAsync_WaitCancelledOrDisposedAtShutdown_EndsSilently()
    {
        var notifier = new RecordingResidencyChangeNotifier();

        await ResidencyExitNotification.TickOnExitAsync(Task.FromCanceled<bool>(new CancellationToken(canceled: true)), notifier, NullLogger.Instance, "test-server");
        await ResidencyExitNotification.TickOnExitAsync(Task.FromException<bool>(new ObjectDisposedException("handle")), notifier, NullLogger.Instance, "test-server");

        AssertEx.Equal(expected: 0, notifier.Count, "Shutdown ends the wait without a residency tick.");
    }

    [Test]
    public async Task TickOnExitAsync_WaitFaults_NeverFaultsAndTicksAnyway()
    {
        var notifier = new RecordingResidencyChangeNotifier();

        await ResidencyExitNotification.TickOnExitAsync(Task.FromException<bool>(new InvalidOperationException("wait failed")), notifier, NullLogger.Instance, "test-server");

        AssertEx.Equal(expected: 1, notifier.Count, "An unknown child state is answered with a re-read.");
    }
}
