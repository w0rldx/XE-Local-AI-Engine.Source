namespace XE_Local_AI_Engine.Tests.AppUpdate;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Tests.CodexOAuth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Covers anonymous public-update orchestration without any GitHub credential dependency.</summary>
public sealed class AppUpdateServiceTests
{
    [Test]
    public void Constructor_ConfiguredDesktop_PrimesImmediateStatusWithoutCheckingTheNetwork()
    {
        var manager = ManagerReturning(new VelopackCheckResult(VelopackCheckOutcome.UpToDate, null));
        manager.CurrentVersion.Returns("0.1.0-rc.5.2");
        var factory = FactoryReturning(manager);
        var state = new AppUpdateState();

        using var service = CreateService(factory, isDesktop: true, state: state);

        AssertEx.True(state.Current.IsDesktop);
        AssertEx.True(state.Current.IsConfigured);
        AssertEx.Equal("0.1.0-rc.5.2", state.Current.CurrentVersion);
        AssertEx.Equal(AppUpdateCheckStatus.NotChecked, state.Current.CheckStatus);
        AssertEx.Null(state.Current.LastCheckedUtc);
        AssertEx.False(state.Current.UpdateAvailable);
        factory.Received(1).Create();
        manager.DidNotReceive().CheckForUpdateAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public void Constructor_WhenVersionDiscoveryFails_KeepsDesktopStatusAvailableAndLogsNoSensitiveDetails()
    {
        const string sensitive = "Velopack metadata at /home/private with token=secret";
        var factory = Substitute.For<IVelopackUpdateManagerFactory>();
        factory.Create().Returns(_ => throw new InvalidOperationException(sensitive));
        var logger = new CapturingLogger<AppUpdateService>();
        var state = new AppUpdateState();

        using var service = CreateService(factory, isDesktop: true, logger: logger, state: state);

        AssertEx.True(state.Current.IsDesktop);
        AssertEx.True(state.Current.IsConfigured);
        AssertEx.Equal("0.0.0", state.Current.CurrentVersion);
        AssertEx.Equal(AppUpdateCheckStatus.Failed, state.Current.CheckStatus);
        AssertEx.Null(state.Current.LastCheckedUtc);
        AssertEx.False(logger.AllText.Contains("secret", StringComparison.Ordinal));
        AssertEx.False(logger.AllText.Contains("/home/private", StringComparison.Ordinal));
    }

    [Test]
    public async Task CheckForUpdates_PublicConfiguredBuild_CreatesAnonymousManagerWithoutTokenLookup()
    {
        var manager = ManagerReturning(new VelopackCheckResult(VelopackCheckOutcome.UpToDate, null));
        var factory = FactoryReturning(manager);
        using var service = CreateService(factory, isDesktop: true);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.True(snapshot.IsConfigured);
        AssertEx.False(snapshot.UpdateAvailable);
        AssertEx.Equal(AppUpdateCheckStatus.Ready, snapshot.CheckStatus);
        factory.Received(1).Create();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RefreshIfStale_ManualAndStartupChecksInEitherOrder_RunOneGitHubCheck(bool startupFirst)
    {
        var checkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCheck = new TaskCompletionSource<VelopackCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = Substitute.For<IVelopackUpdateManager>();
        manager.CurrentVersion.Returns("0.1.0");
        manager.CheckForUpdateAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            checkEntered.TrySetResult();
            return releaseCheck.Task;
        });
        var factory = FactoryReturning(manager);
        using var service = CreateService(factory, isDesktop: true);
        using var startup = new AppUpdateCheckService(service,
            NullLogger<AppUpdateCheckService>.Instance,
            TimeSpan.Zero);

        Task startupTask;
        Task<AppUpdateSnapshot> manualTask;
        if (startupFirst)
        {
            startupTask = startup.CheckOnceAsync(CancellationToken.None);
            await checkEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            manualTask = service.RefreshIfStaleAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
        }
        else
        {
            manualTask = service.RefreshIfStaleAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
            await checkEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            startupTask = startup.CheckOnceAsync(CancellationToken.None);
        }

        releaseCheck.SetResult(new VelopackCheckResult(VelopackCheckOutcome.UpToDate, null));

        await startupTask;
        var manualSnapshot = await manualTask;

        factory.Received(1).Create();
        await manager.Received(1).CheckForUpdateAsync(Arg.Any<CancellationToken>());
        AssertEx.Equal(AppUpdateCheckStatus.Ready, manualSnapshot.CheckStatus);
    }

    [Test]
    public async Task CheckForUpdates_WhenNotDesktop_DoesNotCreateManager()
    {
        var factory = Substitute.For<IVelopackUpdateManagerFactory>();
        using var service = CreateService(factory, isDesktop: false);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.False(snapshot.IsDesktop);
        factory.DidNotReceive().Create();
    }

    [Test]
    public async Task CheckForUpdates_WhenUpdateAvailable_RecordsAvailableVersion()
    {
        var manager = ManagerReturning(new VelopackCheckResult(VelopackCheckOutcome.UpdateAvailable, "0.2.0"));
        manager.CurrentVersion.Returns("0.1.0");
        using var service = CreateService(FactoryReturning(manager), isDesktop: true);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.True(snapshot.UpdateAvailable);
        AssertEx.Equal("0.2.0", AssertEx.NotNull(snapshot.AvailableVersion));
    }

    [Test]
    public async Task CheckForUpdates_WhenFeedIsOffline_RecordsOfflineGracefully()
    {
        var manager = ManagerReturning(new VelopackCheckResult(VelopackCheckOutcome.Offline, null));
        using var service = CreateService(FactoryReturning(manager), isDesktop: true);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.Equal(AppUpdateCheckStatus.Offline, snapshot.CheckStatus);
        AssertEx.False(snapshot.UpdateAvailable);
    }

    [Test]
    public async Task CheckForUpdates_WhenManagerReportsMalformedFeed_RecordsFailedAndLogsSafeReason()
    {
        var logger = new CapturingLogger<AppUpdateService>();
        var manager = ManagerReturning(new VelopackCheckResult(VelopackCheckOutcome.Failed,
            null,
            AppUpdateFailureReason.MalformedFeed));
        using var service = CreateService(FactoryReturning(manager), isDesktop: true, logger: logger);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.Equal(AppUpdateCheckStatus.Failed, snapshot.CheckStatus);
        AssertEx.Contains(logger.AllText, nameof(AppUpdateFailureReason.MalformedFeed));
    }

    [Test]
    public async Task CheckForUpdates_WhenUnexpectedFailureEscapesManager_IsFailedAndLogsNoSensitiveDetails()
    {
        const string sensitive = "https://github.com/example/public-repo?token=secret at /home/operator/private";
        var manager = Substitute.For<IVelopackUpdateManager>();
        manager.CurrentVersion.Returns("0.1.0");
        manager.CheckForUpdateAsync(Arg.Any<CancellationToken>())
               .Returns<Task<VelopackCheckResult>>(_ => throw new FormatException(sensitive));
        var logger = new CapturingLogger<AppUpdateService>();
        using var service = CreateService(FactoryReturning(manager), isDesktop: true, logger: logger);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.Equal(AppUpdateCheckStatus.Failed, snapshot.CheckStatus);
        AssertEx.Contains(logger.AllText, nameof(AppUpdateFailureReason.Unexpected));
        AssertEx.False(logger.AllText.Contains("secret", StringComparison.Ordinal));
        AssertEx.False(logger.AllText.Contains("/home/operator/private", StringComparison.Ordinal));
        AssertEx.False(logger.AllText.Contains("public-repo", StringComparison.Ordinal));
    }

    [Test]
    public async Task Apply_PublicConfiguredBuild_UsesAnonymousManager()
    {
        var manager = Substitute.For<IVelopackUpdateManager>();
        manager.CurrentVersion.Returns("0.1.0");
        manager.PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(false);
        var factory = FactoryReturning(manager);
        var state = new AppUpdateState();
        state.Store(new AppUpdateSnapshot("0.1.0",
            "0.2.0",
            UpdateAvailable: true,
            IsConfigured: true,
            IsDesktop: true,
            CheckStatus: AppUpdateCheckStatus.Ready,
            LastCheckedUtc: DateTimeOffset.UtcNow));
        using var service = CreateService(factory, isDesktop: true, state: state);

        var applying = await service.ApplyAsync(CancellationToken.None);

        AssertEx.False(applying);
        AssertEx.False(state.Current.UpdateAvailable);
        AssertEx.Null(state.Current.AvailableVersion);
        AssertEx.Equal(AppUpdateCheckStatus.Ready, state.Current.CheckStatus);
        factory.Received(1).Create();
    }

    [Test]
    public async Task Apply_WhenManagerThrows_SurfacesSanitizedError_AndLogsNoSensitiveDetails()
    {
        const string sensitive = "download failed at /home/secret/path with token";
        var manager = Substitute.For<IVelopackUpdateManager>();
        manager.PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
               .Returns<Task<bool>>(_ => throw new InvalidOperationException(sensitive));
        var logger = new CapturingLogger<AppUpdateService>();
        var state = AvailableUpdateState();
        using var service = CreateService(FactoryReturning(manager), isDesktop: true, logger: logger, state: state);

        var exception = await AssertEx.ThrowsAsync<AppUpdateException>(() => service.ApplyAsync(CancellationToken.None));

        AssertEx.False(exception.Message.Contains("token", StringComparison.Ordinal));
        AssertEx.False(exception.Message.Contains("/home/secret/path", StringComparison.Ordinal));
        AssertEx.False(logger.AllText.Contains("token", StringComparison.Ordinal));
        AssertEx.False(logger.AllText.Contains("/home/secret/path", StringComparison.Ordinal));
    }

    [Test]
    public async Task Apply_ConcurrentRequests_RunOneVelopackOperation()
    {
        var applyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = Substitute.For<IVelopackUpdateManager>();
        manager.CurrentVersion.Returns("0.1.0");
        manager.PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
               .Returns(_ =>
               {
                   applyEntered.TrySetResult();
                   return releaseApply.Task;
               });
        var factory = FactoryReturning(manager);
        using var service = CreateService(factory, isDesktop: true, state: AvailableUpdateState());

        var first = service.ApplyAsync(CancellationToken.None);
        await applyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.ApplyAsync(CancellationToken.None);
        factory.Received(1).Create();
        await manager.Received(1).PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        releaseApply.SetResult(true);

        AssertEx.True(await first);
        AssertEx.False(await second);
        factory.Received(1).Create();
        await manager.Received(1).PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ApplyAndCheck_UseOneExclusiveVelopackOperationGate()
    {
        var applyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = Substitute.For<IVelopackUpdateManager>();
        manager.CurrentVersion.Returns("0.1.0");
        manager.PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
               .Returns(_ =>
               {
                   applyEntered.TrySetResult();
                   return releaseApply.Task;
               });
        manager.CheckForUpdateAsync(Arg.Any<CancellationToken>())
               .Returns(new VelopackCheckResult(VelopackCheckOutcome.UpToDate, null));
        var factory = FactoryReturning(manager);
        using var service = CreateService(factory, isDesktop: true, state: AvailableUpdateState());

        var apply = service.ApplyAsync(CancellationToken.None);
        await applyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var check = service.CheckForUpdatesAsync(CancellationToken.None);
        await manager.DidNotReceive().CheckForUpdateAsync(Arg.Any<CancellationToken>());
        releaseApply.SetResult(false);

        AssertEx.False(await apply);
        AssertEx.Equal(AppUpdateCheckStatus.Ready, (await check).CheckStatus);
        await manager.Received(1).PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await manager.Received(1).CheckForUpdateAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CheckForUpdates_WhenBuildIsNotConfigured_MakesNoGitHubCall()
    {
        var factory = Substitute.For<IVelopackUpdateManagerFactory>();
        using var service = CreateService(factory, isDesktop: true, repoUrl: "");

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.False(snapshot.IsConfigured);
        AssertEx.False(snapshot.UpdateAvailable);
        AssertEx.Equal(AppUpdateCheckStatus.NotChecked, snapshot.CheckStatus);
        factory.DidNotReceive().Create();
    }

    private static IVelopackUpdateManager ManagerReturning(VelopackCheckResult result)
    {
        var manager = Substitute.For<IVelopackUpdateManager>();
        manager.CurrentVersion.Returns("0.1.0");
        manager.CheckForUpdateAsync(Arg.Any<CancellationToken>()).Returns(result);
        return manager;
    }

    private static AppUpdateState AvailableUpdateState()
    {
        var state = new AppUpdateState();
        state.Store(new AppUpdateSnapshot("0.1.0",
            "0.2.0",
            UpdateAvailable: true,
            IsConfigured: true,
            IsDesktop: true,
            CheckStatus: AppUpdateCheckStatus.Ready,
            LastCheckedUtc: DateTimeOffset.UtcNow));
        return state;
    }

    private static IVelopackUpdateManagerFactory FactoryReturning(IVelopackUpdateManager manager)
    {
        var factory = Substitute.For<IVelopackUpdateManagerFactory>();
        factory.Create().Returns(manager);
        return factory;
    }

    private static AppUpdateService CreateService(IVelopackUpdateManagerFactory factory,
        bool isDesktop,
        string repoUrl = "https://github.com/example/public-repo",
        Microsoft.Extensions.Logging.ILogger<AppUpdateService>? logger = null,
        AppUpdateState? state = null)
    {
        var options = Options.Create(new AppUpdateChannelOptions
        {
            GitHubRepositoryUrl = repoUrl,
            ReleaseTrack = AppUpdateReleaseTrack.Stable
        });

        return new AppUpdateService(factory,
            state ?? new AppUpdateState(),
            options,
            new AppUpdateHostContext(isDesktop, RestartArgs: ["--desktop"]),
            logger ?? NullLogger<AppUpdateService>.Instance);
    }
}
