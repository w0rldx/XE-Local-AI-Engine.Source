namespace XE_Local_AI_Engine.Tests.AppUpdate;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>
///     The startup app-update check runs exactly one check (delegating to <see cref="IAppUpdateService" />) and never
///     crashes startup when the service throws — it degrades to a logged warning.
/// </summary>
public sealed class AppUpdateCheckServiceTests
{
    [Test]
    public async Task CheckOnce_DelegatesToUpdateService()
    {
        var updateService = Substitute.For<IAppUpdateService>();
        updateService.CheckForUpdatesAsync(Arg.Any<CancellationToken>()).Returns(AppUpdateSnapshot.Empty);
        using var service = new AppUpdateCheckService(updateService, NullLogger<AppUpdateCheckService>.Instance, TimeSpan.Zero);

        await service.CheckOnceAsync(CancellationToken.None);

        await updateService.Received(1).CheckForUpdatesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CheckOnce_WhenServiceThrows_DoesNotPropagate()
    {
        var updateService = Substitute.For<IAppUpdateService>();
        updateService.CheckForUpdatesAsync(Arg.Any<CancellationToken>())
                     .Returns<Task<AppUpdateSnapshot>>(_ => throw new InvalidOperationException("boom"));
        using var service = new AppUpdateCheckService(updateService, NullLogger<AppUpdateCheckService>.Instance, TimeSpan.Zero);

        // Must not throw — the startup check is offline/failure-tolerant.
        await service.CheckOnceAsync(CancellationToken.None);

        await updateService.Received(1).CheckForUpdatesAsync(Arg.Any<CancellationToken>());
    }
}
