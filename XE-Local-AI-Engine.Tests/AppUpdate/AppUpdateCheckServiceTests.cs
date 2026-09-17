namespace XE_Local_AI_Engine.Tests.AppUpdate;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The startup app-update check runs exactly one check (delegating to <see cref="IAppUpdateService" />) and never
///     crashes startup when the service throws — it degrades to a logged warning.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class AppUpdateCheckServiceTests
{
    [Test]
    public async Task CheckOnce_DelegatesToUpdateService()
    {
        var updateService = Substitute.For<IAppUpdateService>();
        updateService.RefreshIfStaleAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                     .Returns(AppUpdateSnapshot.Empty);
        using var service = NewService(updateService, StubNodeRuntimeSettings.Create().Build(), TimeProvider.System);

        await service.CheckOnceAsync(CancellationToken.None);

        await updateService.Received(1)
                           .RefreshIfStaleAsync(AppUpdateCheckService.DefaultMinimumCheckInterval,
                               Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CheckOnce_WhenServiceThrows_DoesNotPropagate()
    {
        var updateService = Substitute.For<IAppUpdateService>();
        updateService.RefreshIfStaleAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                     .Returns<Task<AppUpdateSnapshot>>(_ => throw new InvalidOperationException("boom"));
        using var service = NewService(updateService, StubNodeRuntimeSettings.Create().Build(), TimeProvider.System);

        // Must not throw — the startup check is offline/failure-tolerant.
        await service.CheckOnceAsync(CancellationToken.None);

        await updateService.Received(1)
                           .RefreshIfStaleAsync(AppUpdateCheckService.DefaultMinimumCheckInterval,
                               Arg.Any<CancellationToken>());
    }

    // Brief §5 test 1: the observable effect is that nothing is refreshed, not that a line was logged. An offline node's
    // update snapshot must stay empty rather than reading as "checked, none found".
    [Test]
    public async Task Execute_WhenApplicationUpdateChecksAreDisabled_NeverRefreshes()
    {
        var updateService = Substitute.For<IAppUpdateService>();
        var runtimeSettings = StubNodeRuntimeSettings.Create()
                                                     .WithExternalAccessProfile(StoredNodeSettings.ExternalAccessProfileOffline)
                                                     .WithAutoCheckApplicationUpdates(false)
                                                     .Build();
        using var service = NewService(updateService, runtimeSettings, new ManualTimeProvider());

        await BackgroundServiceTestHelper.RunExecuteAsync(service, CancellationToken.None);

        await updateService.DidNotReceiveWithAnyArgs().RefreshIfStaleAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    // Brief §5 test 2, on the fake clock: nothing may be fetched while the operator has not chosen, and the check must
    // start on its own once they do.
    [Test]
    public async Task Execute_WhileTheProfileIsUndecided_Waits_ThenChecksOnceItIsDecided()
    {
        await AssertTheGateWaitsThenChecksAsync(undecidedProfile: null);
    }

    // R5a: "pending" (an administrator exists, the choice has not been made) gates exactly as null does.
    [Test]
    public async Task Execute_WhileTheProfileIsPending_Waits()
    {
        await AssertTheGateWaitsThenChecksAsync(StoredNodeSettings.ExternalAccessProfilePending);
    }

    private static async Task AssertTheGateWaitsThenChecksAsync(string? undecidedProfile)
    {
        var updateService = Substitute.For<IAppUpdateService>();
        updateService.RefreshIfStaleAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(AppUpdateSnapshot.Empty);

        var decided = false;
        var runtimeSettings = StubNodeRuntimeSettings.Create()
                                                     .WithExternalAccessProfileRead(_ => Task.FromResult(decided
                                                         ? StoredNodeSettings.ExternalAccessProfileRecommended
                                                         : undecidedProfile))
                                                     .Build();
        var timeProvider = new ManualTimeProvider();
        var service = NewService(updateService, runtimeSettings, timeProvider);
        try
        {
            var run = BackgroundServiceTestHelper.RunExecuteAsync(service, CancellationToken.None);

            // The gate must have ARMED its poll timer before the clock moves, or the advance passes a window nothing
            // was waiting on and the wait never ends.
            await AssertEx.EventuallyAsync(() => timeProvider.ArmedTimerCount > 0,
                TimeSpan.FromSeconds(5),
                "The gate must arm its poll timer while the profile is undecided.");
            await AssertEx.SettleAsync();
            await updateService.DidNotReceiveWithAnyArgs().RefreshIfStaleAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());

            decided = true;
            timeProvider.Advance(ExternalAccessGate.PollInterval);

            await AssertEx.EventuallyAsync(() => updateService.ReceivedCalls().Any(),
                TimeSpan.FromSeconds(5),
                "The check must run once the profile is decided.");
            await AssertEx.CompletesAsync(run, TimeSpan.FromSeconds(5), "ExecuteAsync must finish after the one-shot check.");
        }
        finally
        {
            service.Dispose();
        }
    }

    private static AppUpdateCheckService NewService(IAppUpdateService updateService,
        INodeRuntimeSettings runtimeSettings,
        TimeProvider timeProvider)
    {
        return new AppUpdateCheckService(updateService,
            runtimeSettings,
            timeProvider,
            NullLogger<AppUpdateCheckService>.Instance,
            TimeSpan.Zero);
    }
}
