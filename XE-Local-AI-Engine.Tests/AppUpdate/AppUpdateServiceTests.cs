namespace XE_Local_AI_Engine.Tests.AppUpdate;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Tests.CodexOAuth;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>Covers anonymous public-update orchestration without any GitHub credential dependency.</summary>
[Category(TestCategories.Integration)]
public sealed class AppUpdateServiceTests
{
    [Test]
    public void Registration_RetainsOnlyCallerSuppliedSanitizedRestartArguments()
    {
        var builder = Host.CreateApplicationBuilder();
        var sanitized = DesktopLaunch.BuildRestartArguments(["--setup", "--admin-password", "secret", "--mcp-only", "--port", "41234"],
            LaunchMode.McpOnly,
            port: 41234);

        builder.Configuration[DesktopBootstrap.NodeDataDirectoryKey] = Path.GetTempPath();
        builder.AddAppUpdate(builder.Configuration, LaunchMode.McpOnly, sanitized, shellOwned: true);

        var descriptor = builder.Services.Single(static service => service.ServiceType == typeof(AppUpdateHostContext));
        var context = (AppUpdateHostContext)AssertEx.NotNull(descriptor.ImplementationInstance);
        AssertEx.True(context.IsShellOwned);
        AssertEx.Equal(Path.GetTempPath(), context.DataDirectory);
        AssertEx.True(context.RestartArgs.SequenceEqual(["--mcp-only", "--port", "41234"], StringComparer.Ordinal));
        AssertEx.False(context.RestartArgs.Any(static value => value.Contains("secret", StringComparison.Ordinal)));
    }

    [Test]
    public void Constructor_ConfiguredDesktop_PrimesImmediateStatusWithoutCheckingTheNetwork()
    {
        var manager = ManagerReturning(new VelopackCheckResult { Outcome = VelopackCheckOutcome.UpToDate, AvailableVersion = null });
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
        factory.Received(1).Create(Arg.Any<AppUpdateFeed>());
        manager.DidNotReceive().CheckForUpdateAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public void Constructor_WhenVersionDiscoveryFails_KeepsDesktopStatusAvailableAndLogsNoSensitiveDetails()
    {
        const string sensitive = "Velopack metadata at /home/private with token=secret";
        var factory = NewFactory();
        factory.Create(Arg.Any<AppUpdateFeed>()).Returns(_ => throw new InvalidOperationException(sensitive));
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
        var manager = ManagerReturning(new VelopackCheckResult { Outcome = VelopackCheckOutcome.UpToDate, AvailableVersion = null });
        var factory = FactoryReturning(manager);
        using var service = CreateService(factory, isDesktop: true);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.True(snapshot.IsConfigured);
        AssertEx.False(snapshot.UpdateAvailable);
        AssertEx.Equal(AppUpdateCheckStatus.Ready, snapshot.CheckStatus);
        factory.Received(1).Create(Arg.Any<AppUpdateFeed>());
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RefreshIfStale_ManualAndStartupChecksInEitherOrder_RunOneGitHubCheck(bool startupFirst)
    {
        var checkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCheck = new TaskCompletionSource<VelopackCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = NewManager();
        manager.CurrentVersion.Returns("0.1.0");
        manager.CheckForUpdateAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            checkEntered.TrySetResult();
            return releaseCheck.Task;
        });
        var factory = FactoryReturning(manager);
        using var service = CreateService(factory, isDesktop: true);
        using var startup = new AppUpdateCheckService(service,
            StubNodeRuntimeSettings.Create().Build(),
            TimeProvider.System,
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

        releaseCheck.SetResult(new VelopackCheckResult { Outcome = VelopackCheckOutcome.UpToDate, AvailableVersion = null });

        await startupTask;
        var manualSnapshot = await manualTask;

        factory.Received(1).Create(Arg.Any<AppUpdateFeed>());
        await manager.Received(1).CheckForUpdateAsync(Arg.Any<CancellationToken>());
        AssertEx.Equal(AppUpdateCheckStatus.Ready, manualSnapshot.CheckStatus);
    }

    [Test]
    public async Task CheckForUpdates_WhenNotDesktop_DoesNotCreateManager()
    {
        var factory = NewFactory();
        using var service = CreateService(factory, isDesktop: false);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.False(snapshot.IsDesktop);
        factory.DidNotReceive().Create(Arg.Any<AppUpdateFeed>());
    }

    [Test]
    public async Task CheckForUpdates_WhenUpdateAvailable_RecordsAvailableVersion()
    {
        var manager = ManagerReturning(new VelopackCheckResult { Outcome = VelopackCheckOutcome.UpdateAvailable, AvailableVersion = "0.2.0" });
        manager.CurrentVersion.Returns("0.1.0");
        using var service = CreateService(FactoryReturning(manager), isDesktop: true);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.True(snapshot.UpdateAvailable);
        AssertEx.Equal("0.2.0", AssertEx.NotNull(snapshot.AvailableVersion));
    }

    [Test]
    public async Task CheckForUpdates_WhenFeedIsOffline_RecordsOfflineGracefully()
    {
        var manager = ManagerReturning(new VelopackCheckResult { Outcome = VelopackCheckOutcome.Offline, AvailableVersion = null });
        using var service = CreateService(FactoryReturning(manager), isDesktop: true);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.Equal(AppUpdateCheckStatus.Offline, snapshot.CheckStatus);
        AssertEx.False(snapshot.UpdateAvailable);
    }

    [Test]
    public async Task CheckForUpdates_WhenManagerReportsMalformedFeed_RecordsFailedAndLogsSafeReason()
    {
        var logger = new CapturingLogger<AppUpdateService>();
        var manager = ManagerReturning(new VelopackCheckResult
        {
            Outcome = VelopackCheckOutcome.Failed,
            AvailableVersion = null,
            FailureReason = AppUpdateFailureReason.MalformedFeed
        });
        using var service = CreateService(FactoryReturning(manager), isDesktop: true, logger: logger);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.Equal(AppUpdateCheckStatus.Failed, snapshot.CheckStatus);
        AssertEx.Contains(logger.AllText, nameof(AppUpdateFailureReason.MalformedFeed));
    }

    [Test]
    public async Task CheckForUpdates_WhenUnexpectedFailureEscapesManager_IsFailedAndLogsNoSensitiveDetails()
    {
        const string sensitive = "https://github.com/example/public-repo?token=secret at /home/operator/private";
        var manager = NewManager();
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
        var manager = NewManager();
        manager.CurrentVersion.Returns("0.1.0");
        manager.PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(false);
        var factory = FactoryReturning(manager);
        var state = new AppUpdateState();
        state.Store(new AppUpdateSnapshot
        {
            CurrentVersion = "0.1.0",
            AvailableVersion = "0.2.0",
            UpdateAvailable = true,
            IsConfigured = true,
            IsDesktop = true,
            CheckStatus = AppUpdateCheckStatus.Ready,
            LastCheckedUtc = DateTimeOffset.UtcNow
        });
        using var service = CreateService(factory, isDesktop: true, state: state);

        var applying = await service.ApplyAsync(CancellationToken.None);

        AssertEx.False(applying);
        AssertEx.False(state.Current.UpdateAvailable);
        AssertEx.Null(state.Current.AvailableVersion);
        AssertEx.Equal(AppUpdateCheckStatus.Ready, state.Current.CheckStatus);
        factory.Received(1).Create(Arg.Any<AppUpdateFeed>());
    }

    [Test]
    public async Task Apply_ForwardsOnlyTheSanitizedStableRestartArguments()
    {
        IReadOnlyList<string> captured = [];
        var manager = NewManager();
        manager.CurrentVersion.Returns("0.1.0");
        manager.PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
               .Returns(call =>
               {
                   captured = call.ArgAt<IReadOnlyList<string>>(0);
                   return true;
               });
        var restartArgs = DesktopLaunch.BuildRestartArguments(["--setup", "--admin-password", "secret", "--mcp-key", "agentic", "--no-browser", "--port", "41234"],
            LaunchMode.McpOnly,
            port: 41234);
        using var service = CreateService(FactoryReturning(manager), isDesktop: true, state: AvailableUpdateState(), restartArgs: restartArgs);

        AssertEx.True(await service.ApplyAsync(CancellationToken.None));
        AssertEx.True(captured.SequenceEqual(["--mcp-only", "--no-browser", "--port", "41234"], StringComparer.Ordinal));
        AssertEx.False(captured.Any(static value => value.Contains("secret", StringComparison.Ordinal)));
    }

    [Test]
    public async Task Apply_WhenManagerThrows_SurfacesSanitizedError_AndLogsNoSensitiveDetails()
    {
        const string sensitive = "download failed at /home/secret/path with token";
        var manager = NewManager();
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
        var manager = NewManager();
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
        factory.Received(1).Create(Arg.Any<AppUpdateFeed>());
        await manager.Received(1).PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        releaseApply.SetResult(true);

        AssertEx.True(await first);
        AssertEx.False(await second);
        factory.Received(1).Create(Arg.Any<AppUpdateFeed>());
        await manager.Received(1).PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ApplyAndCheck_UseOneExclusiveVelopackOperationGate()
    {
        var applyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = NewManager();
        manager.CurrentVersion.Returns("0.1.0");
        manager.PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
               .Returns(_ =>
               {
                   applyEntered.TrySetResult();
                   return releaseApply.Task;
               });
        var factory = FactoryReturning(manager);
        using var service = CreateService(factory, isDesktop: true, state: AvailableUpdateState());

        var apply = service.ApplyAsync(CancellationToken.None);
        await applyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var check = service.CheckForUpdatesAsync(CancellationToken.None);

        // Exactly one check has run while the apply holds the gate: its OWN re-check, which is how the apply finds
        // the manager that offered the winner. The queued check must still be waiting.
        await manager.Received(1).CheckForUpdateAsync(Arg.Any<CancellationToken>());
        releaseApply.SetResult(false);

        AssertEx.False(await apply);
        AssertEx.Equal(AppUpdateCheckStatus.Ready, (await check).CheckStatus);
        await manager.Received(1).PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await manager.Received(2).CheckForUpdateAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CheckForUpdates_WhenBuildIsNotConfigured_MakesNoGitHubCall()
    {
        var factory = NewFactory();
        using var service = CreateService(factory, isDesktop: true, repoUrl: "");

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.False(snapshot.IsConfigured);
        AssertEx.False(snapshot.UpdateAvailable);
        AssertEx.Equal(AppUpdateCheckStatus.NotChecked, snapshot.CheckStatus);
        factory.DidNotReceive().Create(Arg.Any<AppUpdateFeed>());
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Apply_WithNativeWindow_RejectsAttachedEngineButAllowsOwnedEngine(bool shellOwned)
    {
        using var directory = new TempDirectory("xe-update-lease");
        using var shell = OpenShellLease(directory.Path);
        var manager = NewManager();
        manager.PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(true);
        using var service = CreateService(FactoryReturning(manager), isDesktop: true, state: AvailableUpdateState(),
            shellOwned: shellOwned, dataDirectory: directory.Path);

        if (shellOwned)
        {
            AssertEx.True(await service.ApplyAsync(CancellationToken.None));
            await manager.Received(1).PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        }
        else
        {
            var exception = await AssertEx.ThrowsAsync<AppUpdateException>(() => service.ApplyAsync(CancellationToken.None));
            AssertEx.Equal("Close the native XE window, then apply this update from your browser.", exception.Message);
            await manager.DidNotReceive().PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        }
    }

    [Test]
    public async Task Apply_StandaloneRetainsLeaseUntilHostDisposesService()
    {
        using var directory = new TempDirectory("xe-update-lease");
        var manager = NewManager();
        manager.PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(true);
        using (var service = CreateService(FactoryReturning(manager), isDesktop: true, state: AvailableUpdateState(),
                   shellOwned: false, dataDirectory: directory.Path))
        {
            AssertEx.True(await service.ApplyAsync(CancellationToken.None));
            AssertShellLeaseHeld(directory.Path);
            AssertEx.False(await service.ApplyAsync(CancellationToken.None));
        }

        using var released = OpenShellLease(directory.Path);
        AssertEx.True(released.CanWrite);
        await manager.Received(1).PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Apply_ProductionRetentionTransfersLiveLeaseBeyondServiceDisposal()
    {
        using var directory = new TempDirectory("xe-update-lease");
        FileStream? retained = null;
        var transferCount = 0;
        var manager = NewManager();
        manager.PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(true);
        try
        {
            using (var service = CreateService(FactoryReturning(manager), isDesktop: true, state: AvailableUpdateState(),
                       shellOwned: false, dataDirectory: directory.Path, retainAcceptedLease: lease =>
                       {
                           retained = lease;
                           transferCount++;
                       }))
            {
                AssertEx.True(await service.ApplyAsync(CancellationToken.None));
                AssertEx.Equal(directory.FilePath(AppUpdateService.DesktopShellLeaseFileName), AssertEx.NotNull(retained).Name);
            }

            AssertEx.Equal(1, transferCount);
            AssertEx.True(AssertEx.NotNull(retained).CanWrite);
            AssertShellLeaseHeld(directory.Path);
        }
        finally
        {
            if (retained is not null)
            {
                await retained.DisposeAsync();
            }
        }

        using var released = OpenShellLease(directory.Path);
        AssertEx.True(released.CanWrite);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task Apply_StandaloneReleasesLeaseOnNoUpdateFailureOrCancellation(int outcome)
    {
        using var directory = new TempDirectory("xe-update-lease");
        using var cancellation = new CancellationTokenSource();
        var manager = NewManager();
        manager.PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
               .Returns(async _ =>
               {
                   AssertShellLeaseHeld(directory.Path);
                   if (outcome == 1)
                   {
                       throw new AppUpdateException("private manager detail");
                   }

                   if (outcome == 2)
                   {
                       await cancellation.CancelAsync();
                       cancellation.Token.ThrowIfCancellationRequested();
                   }

                   return false;
               });
        using var service = CreateService(FactoryReturning(manager), isDesktop: true, state: AvailableUpdateState(),
            shellOwned: false, dataDirectory: directory.Path);

        if (outcome == 1)
        {
            var exception = await AssertEx.ThrowsAsync<AppUpdateException>(() => service.ApplyAsync(cancellation.Token));
            AssertEx.Equal("The update could not be applied. Please try again later.", exception.Message);
        }
        else if (outcome == 2)
        {
            await AssertEx.ThrowsAsync<OperationCanceledException>(() => service.ApplyAsync(cancellation.Token));
        }
        else
        {
            AssertEx.False(await service.ApplyAsync(cancellation.Token));
        }

        using var released = OpenShellLease(directory.Path);
        AssertEx.True(released.CanWrite);
        await manager.Received(1).PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Apply_StandaloneWithoutDataDirectoryFailsBeforeDownload()
    {
        var manager = NewManager();
        using var service = CreateService(FactoryReturning(manager), isDesktop: true, state: AvailableUpdateState(), shellOwned: false);
        var exception = await AssertEx.ThrowsAsync<AppUpdateException>(() => service.ApplyAsync(CancellationToken.None));
        AssertEx.Equal("The update could not verify the desktop lifetime. Restart XE and try again.", exception.Message);
        await manager.DidNotReceive().PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Apply_WithoutAvailableUpdateDoesNotAcquireShellLease()
    {
        using var directory = new TempDirectory("xe-update-lease");
        var manager = NewManager();
        using var service = CreateService(FactoryReturning(manager), isDesktop: true,
            shellOwned: false, dataDirectory: directory.Path);
        AssertEx.False(await service.ApplyAsync(CancellationToken.None));
        AssertEx.False(File.Exists(directory.FilePath(AppUpdateService.DesktopShellLeaseFileName)));
    }

    private static FileStream OpenShellLease(string directory) =>
        new(Path.Combine(directory, AppUpdateService.DesktopShellLeaseFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static void AssertShellLeaseHeld(string directory)
    {
        AssertEx.Throws<IOException>(() =>
        {
            using var unexpected = OpenShellLease(directory);
        });
    }

    private static IVelopackUpdateManager ManagerReturning(VelopackCheckResult result)
    {
        var manager = NewManager();
        manager.CheckForUpdateAsync(Arg.Any<CancellationToken>()).Returns(result);
        return manager;
    }

    /// <summary>
    ///     A manager substitute that answers a check by default, because ApplyAsync now re-checks under the current
    ///     channel before applying. A test that cares about the outcome still configures its own.
    /// </summary>
    private static IVelopackUpdateManager NewManager()
    {
        var manager = Substitute.For<IVelopackUpdateManager>();
        manager.CurrentVersion.Returns("0.1.0");
        manager.CheckForUpdateAsync(Arg.Any<CancellationToken>())
               .Returns(new VelopackCheckResult { Outcome = VelopackCheckOutcome.UpdateAvailable, AvailableVersion = "0.2.0" });
        return manager;
    }

    private static AppUpdateState AvailableUpdateState()
    {
        var state = new AppUpdateState();
        state.Store(new AppUpdateSnapshot
        {
            CurrentVersion = "0.1.0",
            AvailableVersion = "0.2.0",
            UpdateAvailable = true,
            IsConfigured = true,
            IsDesktop = true,
            CheckStatus = AppUpdateCheckStatus.Ready,
            LastCheckedUtc = DateTimeOffset.UtcNow
        });
        return state;
    }

    [Test]
    [Arguments("1.0.0-rc.3", "1.0.0-rc.2.dev.20260922.1", "1.0.0-rc.3", AppUpdateChannel.Preview)]
    [Arguments("1.0.0-rc.2", "1.0.0-rc.2.dev.20260922.1", "1.0.0-rc.2.dev.20260922.1", AppUpdateChannel.Development)]
    [Arguments("1.0.0", null, "1.0.0", AppUpdateChannel.Stable)]
    public async Task CheckForUpdates_ForDevelopment_OffersTheStrictlyHighestVersionAcrossBothFeeds(
        string mainVersion, string? devVersion, string expectedVersion, AppUpdateChannel expectedChannel)
    {
        var factory = DevelopmentFactory(Offering(mainVersion), devVersion is null ? UpToDate() : Offering(devVersion));
        using var service = CreateService(factory, isDesktop: true, settingsStore: StoreWith(AppUpdateChannelNames.Development));

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.True(snapshot.UpdateAvailable);
        AssertEx.Equal(expectedVersion, snapshot.AvailableVersion);
        AssertEx.Equal<AppUpdateChannel?>(expectedChannel, snapshot.AvailableChannel);
        AssertEx.Equal(AppUpdateChannel.Development, snapshot.SelectedChannel);
    }

    [Test]
    public async Task CheckForUpdates_ForDevelopment_WhenBothFeedsOfferTheSameVersion_ReportsOneOfferFromTheMainFeed()
    {
        // A win requires a STRICTLY higher version, so the tie keeps the earlier (main) feed and produces exactly
        // one offer rather than two.
        var factory = DevelopmentFactory(Offering("1.0.0-rc.3"), Offering("1.0.0-rc.3"));
        using var service = CreateService(factory, isDesktop: true, settingsStore: StoreWith(AppUpdateChannelNames.Development));

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.True(snapshot.UpdateAvailable);
        AssertEx.Equal("1.0.0-rc.3", snapshot.AvailableVersion);
        AssertEx.Equal<AppUpdateChannel?>(AppUpdateChannel.Preview, snapshot.AvailableChannel);
    }

    [Test]
    public async Task CheckForUpdates_ForDevelopment_WhenNeitherFeedIsNewer_ReportsNoUpdate()
    {
        var factory = DevelopmentFactory(UpToDate(), UpToDate());
        using var service = CreateService(factory, isDesktop: true, settingsStore: StoreWith(AppUpdateChannelNames.Development));

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.False(snapshot.UpdateAvailable);
        AssertEx.Null(snapshot.AvailableVersion);
        AssertEx.Null(snapshot.AvailableChannel);
        AssertEx.Equal(AppUpdateCheckStatus.Ready, snapshot.CheckStatus);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Apply_ForDevelopment_GoesThroughTheManagerThatFoundTheWinner(bool devFeedWins)
    {
        // Applying a dev-feed winner through the main-feed manager would re-check the main feed, find nothing and
        // return false — the update would silently never install.
        var main = Offering(devFeedWins ? "1.0.0-rc.2" : "1.0.0-rc.3");
        var dev = Offering("1.0.0-rc.2.dev.20260922.1");
        main.PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(true);
        dev.PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(true);
        var factory = DevelopmentFactory(main, dev);
        using var service = CreateService(factory, isDesktop: true,
            state: AvailableUpdateState(),
            settingsStore: StoreWith(AppUpdateChannelNames.Development));

        AssertEx.True(await service.ApplyAsync(CancellationToken.None));

        var winner = devFeedWins ? dev : main;
        var loser = devFeedWins ? main : dev;
        await winner.Received(1).PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await loser.DidNotReceive().PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CheckForUpdates_ReportsTheRecommendedVersionFromTheMainFeed()
    {
        // Fails if anyone reads results.Last(): the dev feed reports a different recommended version.
        var main = Offering("1.0.0-rc.3", recommendedVersion: "0.9.0");
        var dev = Offering("1.0.0-rc.2.dev.20260922.1", recommendedVersion: "0.1.0");
        var factory = DevelopmentFactory(main, dev);
        using var service = CreateService(factory, isDesktop: true, settingsStore: StoreWith(AppUpdateChannelNames.Development));

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.Equal("0.9.0", snapshot.RecommendedVersion);
    }

    [Test]
    public async Task CheckForUpdates_WhenTheDevelopmentFeedFails_StillOffersTheMainFeedUpdate()
    {
        var dev = NewManager();
        dev.CheckForUpdateAsync(Arg.Any<CancellationToken>())
           .Returns(new VelopackCheckResult
           {
               Outcome = VelopackCheckOutcome.Failed,
               AvailableVersion = null,
               FailureReason = AppUpdateFailureReason.Http
           });
        var factory = DevelopmentFactory(Offering("1.0.0-rc.3"), dev);
        using var service = CreateService(factory, isDesktop: true, settingsStore: StoreWith(AppUpdateChannelNames.Development));

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.True(snapshot.UpdateAvailable);
        AssertEx.Equal("1.0.0-rc.3", snapshot.AvailableVersion);
        AssertEx.Equal(AppUpdateCheckStatus.Ready, snapshot.CheckStatus);
    }

    [Test]
    [Arguments(AppUpdateChannel.Stable)]
    [Arguments(AppUpdateChannel.Preview)]
    [Arguments(AppUpdateChannel.Development)]
    public async Task CheckForUpdates_WhenNotInstalled_EveryChannelReportsUpToDate(AppUpdateChannel channel)
    {
        // A raw-exe / dev run: every manager short-circuits, so no feed is read on any channel.
        var factory = NewFactory();
        factory.Create(Arg.Any<AppUpdateFeed>()).Returns(_ => UpToDate());
        using var service = CreateService(factory, isDesktop: true,
            settingsStore: StoreWith(AppUpdateChannelNames.ToWire(channel)));

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.False(snapshot.UpdateAvailable);
        AssertEx.Null(snapshot.AvailableChannel);
        AssertEx.Null(snapshot.RecommendedVersion);
        AssertEx.Equal(AppUpdateCheckStatus.Ready, snapshot.CheckStatus);
        AssertEx.Equal(channel, snapshot.SelectedChannel);
    }

    [Test]
    [Arguments(AppUpdateChannel.Stable)]
    [Arguments(AppUpdateChannel.Preview)]
    public async Task CheckForUpdates_ForStableAndPreview_NeverCreatesADevelopmentManager(AppUpdateChannel channel)
    {
        var created = new List<AppUpdateFeed>();
        var factory = NewFactory();
        factory.Create(Arg.Any<AppUpdateFeed>()).Returns(call =>
        {
            created.Add(call.Arg<AppUpdateFeed>());
            return UpToDate();
        });
        using var service = CreateService(factory, isDesktop: true,
            settingsStore: StoreWith(AppUpdateChannelNames.ToWire(channel)));

        await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.NotEmpty(created);
        foreach (var feed in created)
        {
            AssertEx.False(feed.VelopackChannel.EndsWith(AppUpdateChannelPolicy.DevelopmentChannelSuffix, StringComparison.Ordinal),
                $"{channel} created a manager for '{feed.VelopackChannel}'.");
        }
    }

    [Test]
    [Arguments(AppUpdateChannel.Stable, 1)]
    [Arguments(AppUpdateChannel.Preview, 1)]
    [Arguments(AppUpdateChannel.Development, 2)]
    public async Task CheckForUpdates_WhenNoChannelIsStored_FollowsTheBakedDefault(AppUpdateChannel bakedDefault, int expectedFeeds)
    {
        var created = new List<AppUpdateFeed>();
        var factory = NewFactory();
        factory.Create(Arg.Any<AppUpdateFeed>()).Returns(call =>
        {
            created.Add(call.Arg<AppUpdateFeed>());
            return UpToDate();
        });
        using var service = CreateService(factory, isDesktop: true,
            settingsStore: new FakeNodeSettingsStore(new StoredNodeSettings { UpdateChannel = null }),
            defaultChannel: bakedDefault);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.Equal(bakedDefault, snapshot.SelectedChannel);
        AssertEx.Equal(bakedDefault, snapshot.DefaultChannel);
        AssertEx.Equal(expectedFeeds, created.Count);
    }

    [Test]
    public async Task CheckForUpdates_WhenAChannelIsStored_OverridesTheBakedDefault()
    {
        var created = new List<AppUpdateFeed>();
        var factory = NewFactory();
        factory.Create(Arg.Any<AppUpdateFeed>()).Returns(call =>
        {
            created.Add(call.Arg<AppUpdateFeed>());
            return UpToDate();
        });
        using var service = CreateService(factory, isDesktop: true,
            settingsStore: StoreWith(AppUpdateChannelNames.Development),
            defaultChannel: AppUpdateChannel.Stable);

        // The constructor primes a manager for the BAKED channel's main feed. Clearing here scopes the assertion to
        // the feeds the check itself asked for, which is the stored channel's plan, not the baked one's.
        created.Clear();

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.Equal(AppUpdateChannel.Development, snapshot.SelectedChannel);
        AssertEx.Equal(AppUpdateChannel.Stable, snapshot.DefaultChannel);
        AssertEx.Equal("win,win-dev", string.Join(',', created.Select(feed => feed.VelopackChannel)));
    }

    [Test]
    public async Task SetChannel_PersistsTheChoiceThroughTheStore()
    {
        // The real store over a real directory, then a FRESH store over the same directory: that is the
        // node-settings.json round trip, not a fake's field.
        using var root = new TempDirectory();
        var factory = DevelopmentFactory(UpToDate(), UpToDate());
        using (var store = NewNodeSettingsStore(root))
        {
            using var service = CreateService(factory, isDesktop: true, settingsStore: store);

            await service.SetChannelAsync(AppUpdateChannel.Development, CancellationToken.None);
        }

        using var reader = NewNodeSettingsStore(root);
        AssertEx.Equal(AppUpdateChannelNames.Development, (await reader.LoadAsync()).UpdateChannel);
    }

    [Test]
    public async Task SetChannel_RunsAnImmediateCheckDespiteTheRateFloor()
    {
        // The primed snapshot was just checked, so RefreshIfStaleAsync(10 min) would serve the cache. The channel
        // endpoint must not be routed through it: a check under a NEW policy is not a duplicate.
        var main = UpToDate();
        var dev = UpToDate();
        var factory = DevelopmentFactory(main, dev);
        using var service = CreateService(factory, isDesktop: true,
            state: AvailableUpdateState(),
            settingsStore: new FakeNodeSettingsStore(new StoredNodeSettings()));

        await service.SetChannelAsync(AppUpdateChannel.Development, CancellationToken.None);

        await main.Received(1).CheckForUpdateAsync(Arg.Any<CancellationToken>());
        await dev.Received(1).CheckForUpdateAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetChannel_NeverApplies()
    {
        var manager = Offering("9.9.9");
        var factory = FactoryReturning(manager);
        using var service = CreateService(factory, isDesktop: true,
            state: AvailableUpdateState(),
            settingsStore: new FakeNodeSettingsStore(new StoredNodeSettings()));

        await service.SetChannelAsync(AppUpdateChannel.Preview, CancellationToken.None);

        await manager.DidNotReceive().PrepareUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetChannel_WhenTheStoreWriteFails_DoesNotCheck()
    {
        // The persist is the authority: a check under a policy that was not stored would report a channel the node
        // does not actually follow.
        var manager = UpToDate();
        var factory = FactoryReturning(manager);
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        store.UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>())
             .Returns<Task<StoredNodeSettings>>(_ => throw new IOException("node-settings.json is read-only"));
        using var service = CreateService(factory, isDesktop: true, settingsStore: store);

        await AssertEx.ThrowsAsync<IOException>(
            () => service.SetChannelAsync(AppUpdateChannel.Development, CancellationToken.None));

        await manager.DidNotReceive().CheckForUpdateAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetStatus_ReportsTheStoredChannelBeforeAnyCheckHasRun()
    {
        // Fails if the endpoint goes back to reading IAppUpdateState.Current raw: the primed snapshot carries the
        // BAKED channel, because the constructor cannot await the settings store.
        var factory = FactoryReturning(UpToDate());
        using var service = CreateService(factory, isDesktop: true,
            settingsStore: StoreWith(AppUpdateChannelNames.Development),
            defaultChannel: AppUpdateChannel.Preview);

        var snapshot = await service.GetStatusAsync(CancellationToken.None);

        AssertEx.Equal(AppUpdateChannel.Development, snapshot.SelectedChannel);
        AssertEx.Equal(AppUpdateChannel.Preview, snapshot.DefaultChannel);
    }

    [Test]
    public async Task GetStatus_WhenTheChannelChangedWithoutACheck_DropsTheOtherChannelsOffer()
    {
        // Node settings expose UpdateChannel for MCP and the settings surfaces (R7), and that path runs no check.
        // Leaving the offer visible would hand a Stable operator an apply button for a Development build.
        var store = StoreWith(AppUpdateChannelNames.Development);
        var factory = DevelopmentFactory(UpToDate(), Offering("1.0.0-rc.2.dev.20260922.1"));
        using var service = CreateService(factory, isDesktop: true, settingsStore: store);

        var offered = await service.CheckForUpdatesAsync(CancellationToken.None);
        AssertEx.True(offered.UpdateAvailable);
        AssertEx.Equal(AppUpdateChannel.Development, offered.AvailableChannel);

        await store.SaveAsync(new StoredNodeSettings { UpdateChannel = AppUpdateChannelNames.Stable });
        var snapshot = await service.GetStatusAsync(CancellationToken.None);

        AssertEx.Equal(AppUpdateChannel.Stable, snapshot.SelectedChannel);
        AssertEx.False(snapshot.UpdateAvailable);
        AssertEx.Null(snapshot.AvailableVersion);
        AssertEx.Null(snapshot.AvailableChannel);
    }

    [Test]
    public async Task RefreshIfStale_WhenTheStoredChannelChangedSinceTheCheck_ChecksAgainUnderTheNewPolicy()
    {
        // The node-settings SAVE path writes the channel and runs no check, so the floor would serve the previous
        // channel's offer to `refresh=true` for up to ten minutes.
        var store = StoreWith(AppUpdateChannelNames.Development);
        var main = UpToDate();
        var dev = Offering("1.0.0-rc.2.dev.20260922.1");
        using var service = CreateService(DevelopmentFactory(main, dev), isDesktop: true, settingsStore: store);

        var offered = await service.CheckForUpdatesAsync(CancellationToken.None);
        AssertEx.Equal(AppUpdateChannel.Development, offered.AvailableChannel);

        await store.SaveAsync(new StoredNodeSettings { UpdateChannel = AppUpdateChannelNames.Stable });
        var refreshed = await service.RefreshIfStaleAsync(TimeSpan.FromMinutes(10), CancellationToken.None);

        // Well inside the floor: the check above stamped LastCheckedUtc with the real clock a moment ago.
        AssertEx.Equal(AppUpdateChannel.Stable, refreshed.SelectedChannel);
        AssertEx.False(refreshed.UpdateAvailable);
        AssertEx.Null(refreshed.AvailableVersion);
        AssertEx.Null(refreshed.AvailableChannel);
        await main.Received(2).CheckForUpdateAsync(Arg.Any<CancellationToken>());
        await dev.Received(1).CheckForUpdateAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RefreshIfStale_WhenTheStoredChannelIsUnchanged_KeepsTheRateFloor()
    {
        // The control for the test above: the floor must still suppress a genuine duplicate.
        var main = UpToDate();
        var dev = Offering("1.0.0-rc.2.dev.20260922.1");
        using var service = CreateService(DevelopmentFactory(main, dev), isDesktop: true,
            settingsStore: StoreWith(AppUpdateChannelNames.Development));

        var first = await service.CheckForUpdatesAsync(CancellationToken.None);
        var second = await service.RefreshIfStaleAsync(TimeSpan.FromMinutes(10), CancellationToken.None);

        AssertEx.Equal("1.0.0-rc.2.dev.20260922.1", first.AvailableVersion);
        AssertEx.Equal("1.0.0-rc.2.dev.20260922.1", second.AvailableVersion);
        AssertEx.Equal(AppUpdateChannel.Development, second.SelectedChannel);
        await main.Received(1).CheckForUpdateAsync(Arg.Any<CancellationToken>());
        await dev.Received(1).CheckForUpdateAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>The REAL node-settings store over a throwaway directory, for the on-disk round trip.</summary>
    private static NodeSettingsStore NewNodeSettingsStore(TempDirectory root)
    {
        return new NodeSettingsStore(new FakeNodeDataDirectory(root.Path), NullLogger<NodeSettingsStore>.Instance);
    }

    /// <summary>A store holding one channel literal, so the service resolves it rather than the baked default.</summary>
    private static FakeNodeSettingsStore StoreWith(string channel)
    {
        return new FakeNodeSettingsStore(new StoredNodeSettings { UpdateChannel = channel });
    }

    /// <summary>A manager reporting an available version, and optionally a newest-stable one from its own feed.</summary>
    private static IVelopackUpdateManager Offering(string availableVersion, string? recommendedVersion = null)
    {
        var manager = NewManager();
        manager.CheckForUpdateAsync(Arg.Any<CancellationToken>())
               .Returns(new VelopackCheckResult
               {
                   Outcome = VelopackCheckOutcome.UpdateAvailable,
                   AvailableVersion = availableVersion,
                   RecommendedVersion = recommendedVersion
               });
        return manager;
    }

    private static IVelopackUpdateManager UpToDate()
    {
        var manager = NewManager();
        manager.CheckForUpdateAsync(Arg.Any<CancellationToken>())
               .Returns(new VelopackCheckResult { Outcome = VelopackCheckOutcome.UpToDate, AvailableVersion = null });
        return manager;
    }

    /// <summary>A factory for the Development plan: the real feed list, the main feed's manager first.</summary>
    private static IVelopackUpdateManagerFactory DevelopmentFactory(IVelopackUpdateManager main, IVelopackUpdateManager dev)
    {
        var factory = NewFactory();
        factory.Create(Arg.Any<AppUpdateFeed>())
               .Returns(call => call.Arg<AppUpdateFeed>().IsMainFeed ? main : dev);
        return factory;
    }

    private static IVelopackUpdateManagerFactory FactoryReturning(IVelopackUpdateManager manager)
    {
        var factory = NewFactory();
        factory.Create(Arg.Any<AppUpdateFeed>()).Returns(manager);
        return factory;
    }

    /// <summary>A factory substitute whose feed plan is the REAL policy, so the tests exercise the shipped mapping.</summary>
    private static IVelopackUpdateManagerFactory NewFactory()
    {
        var factory = Substitute.For<IVelopackUpdateManagerFactory>();
        factory.ResolveFeeds(Arg.Any<AppUpdateChannel>())
               .Returns(call => AppUpdateChannelPolicy.ResolveFeeds(call.Arg<AppUpdateChannel>(), "win"));
        return factory;
    }

    private static AppUpdateService CreateService(IVelopackUpdateManagerFactory factory,
        bool isDesktop,
        string repoUrl = "https://github.com/example/public-repo",
        ILogger<AppUpdateService>? logger = null,
        AppUpdateState? state = null,
        IReadOnlyList<string>? restartArgs = null,
        bool shellOwned = true,
        string? dataDirectory = null,
        Action<FileStream>? retainAcceptedLease = null,
        INodeSettingsStore? settingsStore = null,
        AppUpdateChannel defaultChannel = AppUpdateChannel.Stable)
    {
        var options = Options.Create(new AppUpdateChannelOptions
        {
            GitHubRepositoryUrl = repoUrl,
            DefaultChannel = defaultChannel
        });

        return new AppUpdateService(factory,
            state ?? new AppUpdateState(),
            options,
            new AppUpdateHostContext
            {
                IsLocalMode = isDesktop,
                IsShellOwned = shellOwned,
                DataDirectory = dataDirectory,
                RestartArgs = restartArgs ?? ["--desktop"]
            },
            logger ?? NullLogger<AppUpdateService>.Instance,
            TimeProvider.System,
            settingsStore ?? new FakeNodeSettingsStore(new StoredNodeSettings()),
            retainAcceptedLease);
    }
}
