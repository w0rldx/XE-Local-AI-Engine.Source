namespace XE_Local_AI_Engine.Tests.Hosting;

using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The one-time upgrade backfill for the navigation mode. A node whose operator is past the first-run
///     external-access step and carries no mode keeps today's navigation (<c>advanced</c>); a fresh install and a node
///     still sitting on the external-access chooser are left undecided so the first-run mode step owns the choice; a
///     node that already has a mode is never overwritten; and an unreadable settings file leaves the mode undecided.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class UiModeBackfillTests
{
    [Test]
    [Arguments(StoredNodeSettings.ExternalAccessProfileRecommended)]
    [Arguments(StoredNodeSettings.ExternalAccessProfileOffline)]
    [Arguments(StoredNodeSettings.ExternalAccessProfileCustom)]
    public async Task NodePastTheExternalAccessStepWithNoMode_BackfillsAdvanced(string profile)
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            ExternalAccessProfile = profile
        });
        using var provider = BuildProvider(store, SetupCompleted());

        await Backfill(provider);

        AssertEx.Equal(StoredNodeSettings.UiModeAdvanced, store.Current.UiMode);
    }

    // The population this backfill actually exists for is larger than "has a decided profile": a node upgraded before
    // the external-access feature, and a node whose operator set the three switches by hand, both carry a NULL profile
    // for good (see ExternalAccessProfileBackfillService's remarks). Their operators finished whatever onboarding
    // existed for them, so they must not be asked a first-run question either.
    [Test]
    public async Task NodeWithNoProfileAtAll_BackfillsAdvanced()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var provider = BuildProvider(store, SetupCompleted());

        await Backfill(provider);

        AssertEx.Equal(StoredNodeSettings.UiModeAdvanced, store.Current.UiMode);
    }

    // The interlock with first-run onboarding, and the reason "an administrator exists" alone is NOT the discriminator:
    // the setup endpoint persists the administrator and stamps "pending" in the same call, so a brand-new node whose
    // operator is still looking at the external-access chooser has an administrator. Deciding here would answer the
    // mode question for them and the SPA would never show the step.
    [Test]
    public async Task NodeStillOnTheExternalAccessChooser_IsLeftUndecided()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfilePending
        });
        using var provider = BuildProvider(store, SetupCompleted());

        await Backfill(provider);

        AssertEx.Equal(expected: 0, store.WriteCount);
        AssertEx.Null(store.Current.UiMode);
    }

    [Test]
    public async Task NodeWhoseSetupIsStillRequired_IsLeftUndecided()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var provider = BuildProvider(store, SetupRequired());

        await Backfill(provider);

        AssertEx.Equal(expected: 0, store.WriteCount);
        AssertEx.Null(store.Current.UiMode);
    }

    [Test]
    [Arguments(StoredNodeSettings.UiModeSimple)]
    [Arguments(StoredNodeSettings.UiModeAdvanced)]
    public async Task NodeThatAlreadyHasAMode_IsLeftUntouched(string mode)
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfileRecommended,
            UiMode = mode
        });
        using var provider = BuildProvider(store, SetupCompleted());

        await Backfill(provider);

        AssertEx.Equal(expected: 0, store.WriteCount, "An operator's own choice must never be overwritten, not even with the same value.");
        AssertEx.Equal(mode, store.Current.UiMode);
    }

    [Test]
    public async Task RunTwice_WritesExactlyOnce()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfileRecommended
        });
        using var provider = BuildProvider(store, SetupCompleted());

        await Backfill(provider);
        await Backfill(provider);

        AssertEx.Equal(expected: 1, store.WriteCount, "The second pass must find a mode and return before writing.");
        AssertEx.Equal(StoredNodeSettings.UiModeAdvanced, store.Current.UiMode);
    }

    // Driven against the REAL NodeSettingsStore over a hand-corrupted file, because the behaviour under test is the
    // difference between the tolerant and the strict read and a double is readable by definition.
    [Test]
    public async Task NodeWhoseSettingsFileIsUnreadable_WritesNothing_AndWarnsOnce()
    {
        var root = NewRoot("xe-uimode-backfill-unreadable");
        try
        {
            var settingsPath = Path.Combine(root, "node-settings.json");
            await File.WriteAllTextAsync(settingsPath, "{ \"uiMode\": \"simpl");
            var before = await File.ReadAllBytesAsync(settingsPath);

            using var store = new NodeSettingsStore(new FakeNodeDataDirectory(root), NullLogger<NodeSettingsStore>.Instance);
            using var provider = BuildProvider(store, SetupCompleted());
            var logger = new RecordingLogger<UiModeBackfillService>();

            await UiModeBackfillService.BackfillAsync(provider.GetRequiredService<IServiceScopeFactory>(), logger);

            var after = await File.ReadAllBytesAsync(settingsPath);
            AssertEx.True(before.SequenceEqual(after), "The backfill must not write over an unreadable settings file.");
            AssertEx.Null(await store.LoadStrictAsync(), "The file must still be unreadable, i.e. the mode stays undecided.");
            AssertEx.Equal(expected: 1, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // This runs on the startup path, so an unswallowed failure would stop the host from STARTING, not just skip a
    // background pass. Driven through StartAsync for that reason.
    [Test]
    public async Task WhenTheIdentityReadFails_NoOps_AndDoesNotThrow()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        var authService = Substitute.For<INodeAuthService>();
        authService.GetStatusAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
                   .Returns<Task<NodeAuthStatus>>(_ => throw new InvalidOperationException("The identity database is unreadable."));
        using var provider = BuildProvider(store, authService);
        var service = new UiModeBackfillService(provider.GetRequiredService<IServiceScopeFactory>(),
            new RecordingLogger<UiModeBackfillService>());

        await service.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, store.WriteCount);
        AssertEx.Null(store.Current.UiMode);
    }

    private static Task Backfill(ServiceProvider provider)
    {
        return UiModeBackfillService.BackfillAsync(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
    }

    private static INodeAuthService SetupCompleted()
    {
        return AuthService(setupRequired: false);
    }

    private static INodeAuthService SetupRequired()
    {
        return AuthService(setupRequired: true);
    }

    private static INodeAuthService AuthService(bool setupRequired)
    {
        var authService = Substitute.For<INodeAuthService>();
        authService.GetStatusAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult(new NodeAuthStatus { SetupRequired = setupRequired, Authenticated = false }));
        return authService;
    }

    private static ServiceProvider BuildProvider(INodeSettingsStore settingsStore, INodeAuthService authService)
    {
        var services = new ServiceCollection();
        services.AddSingleton(settingsStore);
        services.AddScoped(_ => authService);
        return services.BuildServiceProvider();
    }

    private static string NewRoot(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        return root;
    }
}
