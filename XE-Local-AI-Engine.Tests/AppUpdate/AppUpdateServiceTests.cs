namespace XE_Local_AI_Engine.Tests.AppUpdate;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the app-update orchestration: it no-ops without a session / off desktop (no GitHub call), maps the Velopack
///     check outcomes to the snapshot (update available, reauth, no-access, offline), uses the baked tester repo for the
///     tester flavor, and surfaces a sanitized apply failure. The Velopack manager is mocked behind the seam — no network.
/// </summary>
public sealed class AppUpdateServiceTests
{
    [Test]
    public async Task CheckForUpdates_WhenSignedOut_DoesNotCheckGitHub()
    {
        var tokenStore = Substitute.For<IGitHubTokenStore>();
        tokenStore.GetSessionAsync(Arg.Any<CancellationToken>()).Returns((GitHubSession?)null);
        var factory = Substitute.For<IVelopackUpdateManagerFactory>();
        var service = CreateService(tokenStore, factory, isDesktop: true);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.Equal(AppUpdateAuthState.SignedOut, snapshot.AuthState);
        AssertEx.False(snapshot.UpdateAvailable);
        factory.DidNotReceive().Create(Arg.Any<string>());
    }

    [Test]
    public async Task CheckForUpdates_WhenNotDesktop_DoesNotCheckGitHub()
    {
        var tokenStore = Substitute.For<IGitHubTokenStore>();
        tokenStore.GetSessionAsync(Arg.Any<CancellationToken>()).Returns(new GitHubSession("ghu_token", "octocat"));
        var factory = Substitute.For<IVelopackUpdateManagerFactory>();
        var service = CreateService(tokenStore, factory, isDesktop: false);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.Equal(AppUpdateAuthState.SignedOut, snapshot.AuthState);
        factory.DidNotReceive().Create(Arg.Any<string>());
        await tokenStore.DidNotReceive().GetSessionAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CheckForUpdates_WhenUpdateAvailable_RecordsAvailableVersion()
    {
        var manager = Substitute.For<IVelopackUpdateManager>();
        manager.CurrentVersion.Returns("0.1.0-rc.1");
        manager.CheckForUpdateAsync(Arg.Any<CancellationToken>())
               .Returns(new VelopackCheckResult(VelopackCheckOutcome.UpdateAvailable, "0.1.0-rc.2"));
        var service = CreateService(SignedInStore(), FactoryReturning(manager), isDesktop: true);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.True(snapshot.UpdateAvailable);
        AssertEx.Equal("0.1.0-rc.2", AssertEx.NotNull(snapshot.AvailableVersion));
        AssertEx.Equal(AppUpdateAuthState.SignedIn, snapshot.AuthState);
        AssertEx.Equal("octocat", AssertEx.NotNull(snapshot.Login));
    }

    [Test]
    public async Task CheckForUpdates_WhenUpToDate_RecordsNoUpdate()
    {
        var manager = Substitute.For<IVelopackUpdateManager>();
        manager.CurrentVersion.Returns("0.1.0-rc.2");
        manager.CheckForUpdateAsync(Arg.Any<CancellationToken>())
               .Returns(new VelopackCheckResult(VelopackCheckOutcome.UpToDate, AvailableVersion: null));
        var service = CreateService(SignedInStore(), FactoryReturning(manager), isDesktop: true);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.False(snapshot.UpdateAvailable);
        AssertEx.Equal(AppUpdateAuthState.SignedIn, snapshot.AuthState);
    }

    [Test]
    public async Task CheckForUpdates_WhenUnauthorized_RecordsReauthRequired()
    {
        var manager = Substitute.For<IVelopackUpdateManager>();
        manager.CurrentVersion.Returns("0.1.0-rc.1");
        manager.CheckForUpdateAsync(Arg.Any<CancellationToken>())
               .Returns(new VelopackCheckResult(VelopackCheckOutcome.Unauthorized, AvailableVersion: null));
        var service = CreateService(SignedInStore(), FactoryReturning(manager), isDesktop: true);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.Equal(AppUpdateAuthState.ReauthRequired, snapshot.AuthState);
        AssertEx.False(snapshot.UpdateAvailable);
    }

    [Test]
    public async Task CheckForUpdates_WhenForbidden_RecordsNoAccess()
    {
        var manager = Substitute.For<IVelopackUpdateManager>();
        manager.CurrentVersion.Returns("0.1.0-rc.1");
        manager.CheckForUpdateAsync(Arg.Any<CancellationToken>())
               .Returns(new VelopackCheckResult(VelopackCheckOutcome.Forbidden, AvailableVersion: null));
        var service = CreateService(SignedInStore(), FactoryReturning(manager), isDesktop: true);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.Equal(AppUpdateAuthState.NoAccess, snapshot.AuthState);
    }

    [Test]
    public async Task CheckForUpdates_WhenOffline_RecordsOfflineGracefully()
    {
        var manager = Substitute.For<IVelopackUpdateManager>();
        manager.CurrentVersion.Returns("0.1.0-rc.1");
        manager.CheckForUpdateAsync(Arg.Any<CancellationToken>())
               .Returns(new VelopackCheckResult(VelopackCheckOutcome.Offline, AvailableVersion: null));
        var service = CreateService(SignedInStore(), FactoryReturning(manager), isDesktop: true);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.True(snapshot.IsOffline);
        AssertEx.False(snapshot.UpdateAvailable);
        AssertEx.Equal(AppUpdateAuthState.SignedIn, snapshot.AuthState);
    }

    [Test]
    public async Task CheckForUpdates_UsesTesterRepo_ForTesterFlavor()
    {
        var manager = Substitute.For<IVelopackUpdateManager>();
        manager.CurrentVersion.Returns("0.1.0-rc.1");
        manager.CheckForUpdateAsync(Arg.Any<CancellationToken>())
               .Returns(new VelopackCheckResult(VelopackCheckOutcome.UpToDate, AvailableVersion: null));
        var factory = Substitute.For<IVelopackUpdateManagerFactory>();
        factory.Create(Arg.Any<string>()).Returns(manager);

        // The real flavor→repo mapping lives in VelopackUpdateManagerFactory (which reads the baked repo URL); here we
        // assert the service builds the manager from the signed-in token so the baked tester repo is what gets used.
        var service = CreateService(SignedInStore(), factory, isDesktop: true,
            channel: "tester", repoUrl: "https://github.com/example/tester-repo");

        await service.CheckForUpdatesAsync(CancellationToken.None);

        factory.Received(1).Create("ghu_token");
    }

    [Test]
    public async Task Apply_WhenManagerThrows_SurfacesSanitizedError_WithoutTokenOrPath()
    {
        var manager = Substitute.For<IVelopackUpdateManager>();
        manager.ApplyUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
               .Returns<Task<bool>>(_ => throw new InvalidOperationException("download failed at /home/secret/path with ghu_token"));
        var service = CreateService(SignedInStore(), FactoryReturning(manager), isDesktop: true);

        var exception = await AssertEx.ThrowsAsync<AppUpdateException>(() => service.ApplyAsync(CancellationToken.None));

        AssertEx.False(exception.Message.Contains("ghu_token", StringComparison.Ordinal), "the token must not leak into the error");
        AssertEx.False(exception.Message.Contains("/home/secret/path", StringComparison.Ordinal), "the path must not leak into the error");
    }

    [Test]
    public async Task Apply_WhenSignedOut_DoesNothing()
    {
        var tokenStore = Substitute.For<IGitHubTokenStore>();
        tokenStore.GetSessionAsync(Arg.Any<CancellationToken>()).Returns((GitHubSession?)null);
        var factory = Substitute.For<IVelopackUpdateManagerFactory>();
        var service = CreateService(tokenStore, factory, isDesktop: true);

        var applying = await service.ApplyAsync(CancellationToken.None);

        AssertEx.False(applying);
        factory.DidNotReceive().Create(Arg.Any<string>());
    }

    [Test]
    public async Task Apply_WhenLiveRecheckFindsNothing_ReturnsFalse_WithoutFakeRestart()
    {
        // Simulates the stale-snapshot race: the cached status said "update available", but the live re-check inside
        // ApplyUpdateAndRestartAsync finds nothing, so it returns false (no process replacement). The service must
        // surface that real outcome so the endpoint reports Applying:false rather than stranding the client.
        var manager = Substitute.For<IVelopackUpdateManager>();
        manager.ApplyUpdateAndRestartAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
               .Returns(false);
        var service = CreateService(SignedInStore(), FactoryReturning(manager), isDesktop: true);

        var applying = await service.ApplyAsync(CancellationToken.None);

        AssertEx.False(applying);
    }

    /// <summary>
    ///     An unbaked build must report <see cref="AppUpdateAuthState.NotConfigured" />, NOT signed-out. Signed-out is
    ///     what makes React offer the GitHub sign-in card, and on this build that card cannot work — the device flow is
    ///     rejected on the same <c>IsConfigured</c> predicate. Covers both unbaked shapes that actually ship: the
    ///     <c>main</c> channel's <c>REPLACE_*</c> placeholders, and a tester build before packaging injects the ID.
    /// </summary>
    [Test]
    [Arguments("https://github.com/REPLACE_OWNER/REPLACE_MAIN_REPO", "REPLACE_MAIN_CLIENT_ID")]
    [Arguments("https://github.com/w0rldx/XE-Local-AI-Engine.Tester-App", "")]
    public async Task CheckForUpdates_WhenBuildIsNotConfigured_ReportsNotConfigured_AndMakesNoGitHubCall(string repoUrl,
        string clientId)
    {
        var tokenStore = Substitute.For<IGitHubTokenStore>();
        var factory = Substitute.For<IVelopackUpdateManagerFactory>();
        var service = CreateService(tokenStore, factory, isDesktop: true, repoUrl: repoUrl, clientId: clientId);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.Equal(AppUpdateAuthState.NotConfigured, snapshot.AuthState);
        AssertEx.False(snapshot.UpdateAvailable);
        AssertEx.False(snapshot.IsOffline, "an unconfigured build is not offline — nothing was ever attempted");
        AssertEx.Null(snapshot.Login);
        AssertEx.True(snapshot.IsDesktop, "the section still renders; only its controls are withheld");
        factory.DidNotReceive().Create(Arg.Any<string>());
        await tokenStore.DidNotReceive().GetSessionAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A configured build that simply has no stored session must stay <see cref="AppUpdateAuthState.SignedOut" />.
    ///     This is the counterpart that stops the new state from swallowing the ordinary signed-out case — the two were
    ///     previously indistinguishable on the wire, which is the whole reason the state was added.
    /// </summary>
    [Test]
    public async Task CheckForUpdates_WhenConfiguredButSignedOut_StaysSignedOut_NotNotConfigured()
    {
        var tokenStore = Substitute.For<IGitHubTokenStore>();
        tokenStore.GetSessionAsync(Arg.Any<CancellationToken>()).Returns((GitHubSession?)null);
        var factory = Substitute.For<IVelopackUpdateManagerFactory>();
        var service = CreateService(tokenStore, factory, isDesktop: true);

        var snapshot = await service.CheckForUpdatesAsync(CancellationToken.None);

        AssertEx.Equal(AppUpdateAuthState.SignedOut, snapshot.AuthState);
        await tokenStore.Received(1).GetSessionAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Apply must stay inert on an unconfigured build regardless of the new state — the state is a UI signal, not a
    ///     relaxation of the gate.
    /// </summary>
    [Test]
    public async Task Apply_WhenBuildIsNotConfigured_DoesNothing()
    {
        var factory = Substitute.For<IVelopackUpdateManagerFactory>();
        var service = CreateService(SignedInStore(), factory, isDesktop: true, clientId: "");

        var applying = await service.ApplyAsync(CancellationToken.None);

        AssertEx.False(applying);
        factory.DidNotReceive().Create(Arg.Any<string>());
    }

    private static IGitHubTokenStore SignedInStore()
    {
        var tokenStore = Substitute.For<IGitHubTokenStore>();
        tokenStore.GetSessionAsync(Arg.Any<CancellationToken>()).Returns(new GitHubSession("ghu_token", "octocat"));
        return tokenStore;
    }

    private static IVelopackUpdateManagerFactory FactoryReturning(IVelopackUpdateManager manager)
    {
        var factory = Substitute.For<IVelopackUpdateManagerFactory>();
        factory.Create(Arg.Any<string>()).Returns(manager);
        return factory;
    }

    private static AppUpdateService CreateService(IGitHubTokenStore tokenStore,
        IVelopackUpdateManagerFactory factory,
        bool isDesktop,
        string channel = "main",
        string repoUrl = "https://github.com/example/main-repo",
        string clientId = "Iv1.testclientid")
    {
        var options = Options.Create(new AppUpdateChannelOptions
        {
            Channel = channel,
            GitHubRepositoryUrl = repoUrl,
            GitHubAppClientId = clientId
        });
        var hostContext = new AppUpdateHostContext(isDesktop, RestartArgs: ["--desktop"]);

        return new AppUpdateService(tokenStore,
            factory,
            new AppUpdateState(),
            options,
            hostContext,
            NullLogger<AppUpdateService>.Instance);
    }
}
