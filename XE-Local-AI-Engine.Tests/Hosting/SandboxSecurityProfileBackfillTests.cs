namespace XE_Local_AI_Engine.Tests.Hosting;

using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The one-time upgrade backfill for the sandbox security profile (ADR 0020): a node past the external-access step with no profile is
///     stamped <c>low</c>, today's behaviour.
/// </summary>
/// <remarks>
///     The navigation-mode backfill's discriminator: a fresh install, a node still on the external-access chooser and a node already
///     holding a literal (<c>pending</c> included) are left alone, and an unreadable settings file is never written over.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class SandboxSecurityProfileBackfillTests
{
    [Test]
    [Arguments(StoredNodeSettings.ExternalAccessProfileRecommended)]
    [Arguments(StoredNodeSettings.ExternalAccessProfileOffline)]
    [Arguments(StoredNodeSettings.ExternalAccessProfileCustom)]
    public async Task NodePastTheExternalAccessStepWithNoProfile_BackfillsLow(string externalAccessProfile)
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            ExternalAccessProfile = externalAccessProfile
        });
        using var provider = BuildProvider(store, SetupCompleted());

        await Backfill(provider);

        AssertEx.Equal(StoredNodeSettings.SandboxSecurityProfileLow, store.Current.SandboxSecurityProfile);
    }

    // A node upgraded before the external-access feature carries a NULL external-access profile for good; its operator finished
    // onboarding, so it must not be asked a first-run question either.
    [Test]
    public async Task NodeWithNoExternalAccessProfileAtAll_BackfillsLow()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var provider = BuildProvider(store, SetupCompleted());

        await Backfill(provider);

        AssertEx.Equal(StoredNodeSettings.SandboxSecurityProfileLow, store.Current.SandboxSecurityProfile);
    }

    // Setup persists the administrator and stamps "pending" in one call, so "an administrator exists" alone would answer the profile
    // question for an operator still looking at the external-access chooser.
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
        AssertEx.Null(store.Current.SandboxSecurityProfile);
    }

    [Test]
    public async Task FreshInstallWhoseSetupIsStillRequired_IsLeftUndecided()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var provider = BuildProvider(store, SetupRequired());

        await Backfill(provider);

        AssertEx.Equal(expected: 0, store.WriteCount);
        AssertEx.Null(store.Current.SandboxSecurityProfile);
    }

    // "pending" is the first-run chooser's to answer, and an operator's own low/high is never overwritten, not even with the same value.
    [Test]
    [Arguments(StoredNodeSettings.SandboxSecurityProfilePending)]
    [Arguments(StoredNodeSettings.SandboxSecurityProfileLow)]
    [Arguments(StoredNodeSettings.SandboxSecurityProfileHigh)]
    public async Task NodeThatAlreadyHasAProfile_IsLeftUntouched(string profile)
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfileRecommended,
            SandboxSecurityProfile = profile
        });
        using var provider = BuildProvider(store, SetupCompleted());

        await Backfill(provider);

        AssertEx.Equal(expected: 0, store.WriteCount);
        AssertEx.Equal(profile, store.Current.SandboxSecurityProfile);
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

        AssertEx.Equal(expected: 1, store.WriteCount, "The second pass must find a profile and return before writing.");
        AssertEx.Equal(StoredNodeSettings.SandboxSecurityProfileLow, store.Current.SandboxSecurityProfile);
    }

    // Against the REAL NodeSettingsStore over a hand-corrupted file: the behaviour under test is the strict read, and a double is
    // readable by definition.
    [Test]
    public async Task NodeWhoseSettingsFileIsUnreadable_WritesNothing_AndWarnsOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "xe-sandbox-profile-backfill-unreadable", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            var settingsPath = Path.Combine(root, "node-settings.json");
            await File.WriteAllTextAsync(settingsPath, "{ \"sandboxSecurityProfile\": \"hig");
            var before = await File.ReadAllBytesAsync(settingsPath);

            using var store = new NodeSettingsStore(new FakeNodeDataDirectory(root), NullLogger<NodeSettingsStore>.Instance);
            using var provider = BuildProvider(store, SetupCompleted());
            var logger = new RecordingLogger<SandboxSecurityProfileBackfillService>();

            await SandboxSecurityProfileBackfillService.BackfillAsync(provider.GetRequiredService<IServiceScopeFactory>(), logger);

            var after = await File.ReadAllBytesAsync(settingsPath);
            AssertEx.True(before.SequenceEqual(after), "The backfill must not write over an unreadable settings file.");
            AssertEx.Null(await store.LoadStrictAsync(), "The file must still be unreadable, i.e. the profile stays undecided.");
            AssertEx.Equal(expected: 1, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Startup path: an unswallowed failure would stop the host from starting, so this is driven through StartAsync.
    [Test]
    public async Task WhenTheIdentityReadFails_NoOps_AndDoesNotThrow()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        var authService = Substitute.For<INodeAuthService>();
        authService.GetStatusAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
                   .Returns<Task<NodeAuthStatus>>(_ => throw new InvalidOperationException("The identity database is unreadable."));
        using var provider = BuildProvider(store, authService);
        var service = new SandboxSecurityProfileBackfillService(provider.GetRequiredService<IServiceScopeFactory>(),
            new RecordingLogger<SandboxSecurityProfileBackfillService>());

        await service.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, store.WriteCount);
        AssertEx.Null(store.Current.SandboxSecurityProfile);
    }

    private static Task Backfill(ServiceProvider provider)
    {
        return SandboxSecurityProfileBackfillService.BackfillAsync(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
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
                   .Returns(Task.FromResult(new NodeAuthStatus
                   {
                       SetupRequired = setupRequired,
                       Authenticated = false
                   }));
        return authService;
    }

    private static ServiceProvider BuildProvider(INodeSettingsStore settingsStore, INodeAuthService authService)
    {
        var services = new ServiceCollection();
        services.AddSingleton(settingsStore);
        services.AddScoped(_ => authService);
        return services.BuildServiceProvider();
    }
}
