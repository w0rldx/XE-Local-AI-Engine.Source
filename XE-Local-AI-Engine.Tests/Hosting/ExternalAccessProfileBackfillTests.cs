namespace XE_Local_AI_Engine.Tests.Hosting;

using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Tests.ExternalProviders;
using XE_Local_AI_Engine.Tests.Providers.OpenAICompat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The one-time upgrade backfill for the external-access profile. A node that already completed setup and predates
///     the profile — i.e. carries none of its four members — keeps today's behaviour (<c>recommended</c>, all three
///     switches on); a fresh install, a node mid-decision and a node holding switches of its own are left for the
///     operator to answer; and an unreadable settings file leaves the profile UNDECIDED rather than silently re-enabling
///     outbound checks that were turned off.
/// </summary>
public sealed class ExternalAccessProfileBackfillTests
{
    [Test]
    public async Task CompletedSetupWithNoProfile_BackfillsRecommendedWithAllThreeSwitchesOn()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var provider = BuildProvider(store, SetupCompleted());

        await Backfill(provider).ConfigureAwait(false);

        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfileRecommended, store.Current.ExternalAccessProfile);
        AssertEx.Equal(expected: true, store.Current.AutoCheckApplicationUpdates);
        AssertEx.Equal(expected: true, store.Current.AutoCheckRuntimeUpdates);
        AssertEx.Equal(expected: true, store.Current.AutoProvisionFirstRunModel);
    }

    [Test]
    public async Task CompletedSetupWithNoProfile_RunTwice_WritesExactlyOnce()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var provider = BuildProvider(store, SetupCompleted());

        await Backfill(provider).ConfigureAwait(false);
        await Backfill(provider).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, store.WriteCount, "The second pass must find a profile and return before writing.");
        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfileRecommended, store.Current.ExternalAccessProfile);
    }

    [Test]
    public async Task NodeThatAlreadyHasAProfile_IsLeftUntouched()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfileOffline,
            AutoCheckApplicationUpdates = false,
            AutoCheckRuntimeUpdates = false,
            AutoProvisionFirstRunModel = false
        });
        using var provider = BuildProvider(store, SetupCompleted());

        await Backfill(provider).ConfigureAwait(false);

        AssertEx.Equal(expected: 0, store.WriteCount);
        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfileOffline, store.Current.ExternalAccessProfile);
        AssertEx.Equal(expected: false, store.Current.AutoCheckRuntimeUpdates);
    }

    // The interlock with first-run setup: "pending" means an administrator exists and the choice has NOT been made, so
    // deciding it here would answer for the operator and hide the profile step the SPA routes to on exactly that value.
    [Test]
    public async Task NodeWhoseProfileIsPending_IsLeftPending()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfilePending
        });
        using var provider = BuildProvider(store, SetupCompleted());

        await Backfill(provider).ConfigureAwait(false);

        AssertEx.Equal(expected: 0, store.WriteCount);
        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfilePending, store.Current.ExternalAccessProfile);
    }

    [Test]
    public async Task NodeWhoseSetupIsStillRequired_IsLeftUndecided()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var provider = BuildProvider(store, SetupRequired());

        await Backfill(provider).ConfigureAwait(false);

        AssertEx.Equal(expected: 0, store.WriteCount);
        AssertEx.Null(store.Current.ExternalAccessProfile);
    }

    // The three below are the operator-opt-out guard: the legacy install this backfill exists for carries NO
    // external-access member, so a record with a switch of its own — with or without a profile the normaliser kept —
    // must be left alone rather than backfilled to all-true. Driven against the REAL NodeSettingsStore because the
    // junk-profile case only exists once NodeSettingsStore.NormalizeExternalAccessProfile has run over a real file.
    [Test]
    public async Task NodeWithExplicitSwitchesAndNoProfile_KeepsItsSwitchesAndStaysUndecided()
    {
        await AssertTheBackfillLeavesTheRecordAlone("xe-backfill-switches-no-profile",
                  """
                  {
                    "autoCheckApplicationUpdates": false,
                    "autoCheckRuntimeUpdates": false,
                    "autoProvisionFirstRunModel": false
                  }
                  """,
                  expectedProfile: null,
                  expectedSwitches: false)
              .ConfigureAwait(false);
    }

    [Test]
    public async Task NodeWithAJunkProfileAndExplicitSwitches_KeepsItsSwitchesAndStaysUndecided()
    {
        // "Offline" is unrecognised — the comparison is ordinal — so the normaliser hands the backfill "pending" beside
        // three false switches. Pending IS undecided: the gated services keep waiting and nothing is stamped.
        await AssertTheBackfillLeavesTheRecordAlone("xe-backfill-junk-profile",
                  """
                  {
                    "externalAccessProfile": "Offline",
                    "autoCheckApplicationUpdates": false,
                    "autoCheckRuntimeUpdates": false,
                    "autoProvisionFirstRunModel": false
                  }
                  """,
                  StoredNodeSettings.ExternalAccessProfilePending,
                  expectedSwitches: false)
              .ConfigureAwait(false);
    }

    // The regression the round-2 review bought: a settings file holding ONLY an unrecognised profile. Nulling it made
    // the record indistinguishable from a legacy install, so the backfill stamped "recommended" plus all three switches
    // on a node whose operator had written a profile. Loading it as "pending" makes it non-null, i.e. left alone.
    [Test]
    public async Task NodeWithAJunkProfileAndNoSwitches_IsReadAsPendingAndNotBackfilled()
    {
        await AssertTheBackfillLeavesTheRecordAlone("xe-backfill-junk-profile-only",
                  """
                  { "externalAccessProfile": "Offline" }
                  """,
                  StoredNodeSettings.ExternalAccessProfilePending,
                  expectedSwitches: null)
              .ConfigureAwait(false);
    }

    [Test]
    public async Task NodeWithOnlyOneSwitchPresent_StaysUndecided()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings { AutoCheckRuntimeUpdates = true });
        using var provider = BuildProvider(store, SetupCompleted());

        await Backfill(provider).ConfigureAwait(false);

        AssertEx.Equal(expected: 0, store.WriteCount, "One member is enough to prove the record is not a legacy install.");
        AssertEx.Null(store.Current.ExternalAccessProfile);
        AssertEx.Null(store.Current.AutoCheckApplicationUpdates);
        AssertEx.Null(store.Current.AutoProvisionFirstRunModel);
    }

    // Driven against the REAL NodeSettingsStore over a hand-corrupted file, because the behaviour under test is the
    // difference between the tolerant and the strict read and a double is readable by definition.
    [Test]
    public async Task NodeWhoseSettingsFileIsUnreadable_WritesNothing_AndWarnsOnce()
    {
        var root = NewRoot("xe-backfill-unreadable");
        try
        {
            var settingsPath = Path.Combine(root, "node-settings.json");
            await File.WriteAllTextAsync(settingsPath, "{ \"externalAccessProfile\": \"offli").ConfigureAwait(false);
            var before = await File.ReadAllBytesAsync(settingsPath).ConfigureAwait(false);

            using var store = new NodeSettingsStore(new FakeNodeDataDirectory(root), NullLogger<NodeSettingsStore>.Instance);
            using var provider = BuildProvider(store, SetupCompleted());
            var logger = new RecordingLogger<ExternalAccessProfileBackfillService>();

            await ExternalAccessProfileBackfillService
                  .BackfillAsync(provider.GetRequiredService<IServiceScopeFactory>(), logger)
                  .ConfigureAwait(false);

            var after = await File.ReadAllBytesAsync(settingsPath).ConfigureAwait(false);
            AssertEx.True(before.SequenceEqual(after), "The backfill must not write over an unreadable settings file.");
            AssertEx.Null(await store.LoadStrictAsync().ConfigureAwait(false),
                "The file must still be unreadable, i.e. the profile is undecided rather than recommended.");
            AssertEx.Equal(expected: 1, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // The composition test for the two halves: an earlier startup writer must not "heal" the corruption into a valid
    // record with a null profile, because the backfill would then decide that null as "recommended" in the same boot and
    // a node the operator had set to Offline would silently start checking again. ExternalProviderStartupReconciler is
    // that writer — it is registered by AddNodeApplication, i.e. BEFORE the hosted-service block this backfill sits in —
    // and it genuinely wants to write here, because a tool-supporting external model is registered and the allow-list
    // the corrupt file reads as is empty.
    [Test]
    public async Task CorruptOfflineFileWithAReconcilableExternalModel_SurvivesStartupByteIdentical()
    {
        var root = NewRoot("xe-backfill-corrupt-offline");
        try
        {
            var settingsPath = Path.Combine(root, "node-settings.json");
            await File.WriteAllTextAsync(settingsPath, "{ \"externalAccessProfile\": \"offline\", \"autoCheckRuntimeUp")
                      .ConfigureAwait(false);
            var before = await File.ReadAllBytesAsync(settingsPath).ConfigureAwait(false);

            using var store = new NodeSettingsStore(new FakeNodeDataDirectory(root), NullLogger<NodeSettingsStore>.Instance);
            var externalStore = new FakeExternalProviderStore(ExternalProviderRegistryTests.Connection(
                ExternalProviderTestData.ConnectionId,
                models: [ExternalProviderTestData.WireId],
                supportsTools: true));
            var mapStore = new InMemoryCoordinatedModelProviderMapStore();
            using var provider = BuildProvider(store, SetupCompleted(), externalStore, mapStore);

            var registryCache = Substitute.For<IExternalProviderRegistryCache>();
            var reconciler = new ExternalProviderStartupReconciler(provider.GetRequiredService<IServiceScopeFactory>(),
                registryCache,
                NullLogger<ExternalProviderStartupReconciler>.Instance);

            await reconciler.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await Backfill(provider).ConfigureAwait(false);

            // Non-vacuity: the reconciler writes its provider-map rows BEFORE the allow-list settings write, so a
            // written ext: row proves the pass really did reach the settings write rather than skipping the file.
            AssertEx.Contains(mapStore.Mappings.Keys, ExternalProviderTestData.ModelId);

            var after = await File.ReadAllBytesAsync(settingsPath).ConfigureAwait(false);
            AssertEx.True(before.SequenceEqual(after),
                "Neither the startup reconciler nor the backfill may write over an unreadable settings file.");
            AssertEx.Null(await store.LoadStrictAsync().ConfigureAwait(false),
                "The profile must stay undecided, so the gated services keep waiting instead of resuming outbound checks.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // This runs on the startup path now, so an unswallowed failure would stop the host from STARTING, not just skip a
    // background pass. Driven through StartAsync for that reason.
    [Test]
    public async Task WhenTheIdentityReadFails_NoOps_AndDoesNotThrow()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        var authService = Substitute.For<INodeAuthService>();
        authService.GetStatusAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
                   .Returns<Task<NodeAuthStatus>>(_ => throw new InvalidOperationException("The identity database is unreadable."));
        using var provider = BuildProvider(store, authService);
        var service = new ExternalAccessProfileBackfillService(provider.GetRequiredService<IServiceScopeFactory>(),
            new RecordingLogger<ExternalAccessProfileBackfillService>());

        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 0, store.WriteCount);
        AssertEx.Null(store.Current.ExternalAccessProfile);
    }

    private static async Task AssertTheBackfillLeavesTheRecordAlone(string prefix, string json, string? expectedProfile, bool? expectedSwitches)
    {
        var root = NewRoot(prefix);
        try
        {
            var settingsPath = Path.Combine(root, "node-settings.json");
            await File.WriteAllTextAsync(settingsPath, json).ConfigureAwait(false);
            var before = await File.ReadAllBytesAsync(settingsPath).ConfigureAwait(false);

            using var store = new NodeSettingsStore(new FakeNodeDataDirectory(root), NullLogger<NodeSettingsStore>.Instance);
            using var provider = BuildProvider(store, SetupCompleted());

            await Backfill(provider).ConfigureAwait(false);

            var after = await File.ReadAllBytesAsync(settingsPath).ConfigureAwait(false);
            AssertEx.True(before.SequenceEqual(after), "The backfill must not write over a record that already carries an external-access member.");

            var stored = AssertEx.NotNull(await store.LoadStrictAsync().ConfigureAwait(false));
            AssertEx.Equal<string?>(expectedProfile, stored.ExternalAccessProfile, "The profile must stay undecided until an operator chooses one.");
            AssertEx.Equal(expectedSwitches, stored.AutoCheckApplicationUpdates);
            AssertEx.Equal(expectedSwitches, stored.AutoCheckRuntimeUpdates);
            AssertEx.Equal(expectedSwitches, stored.AutoProvisionFirstRunModel);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task Backfill(ServiceProvider provider)
    {
        return ExternalAccessProfileBackfillService.BackfillAsync(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance);
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
                   .Returns(Task.FromResult(new NodeAuthStatus(setupRequired, Authenticated: false)));
        return authService;
    }

    private static ServiceProvider BuildProvider(INodeSettingsStore settingsStore,
        INodeAuthService authService,
        IExternalProviderStore? externalProviderStore = null,
        InMemoryCoordinatedModelProviderMapStore? mapStore = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(settingsStore);
        services.AddScoped(_ => authService);

        if (externalProviderStore is not null)
        {
            services.AddSingleton(externalProviderStore);
            services.AddSingleton<ICoordinatedModelProviderMapStore>(mapStore ?? new InMemoryCoordinatedModelProviderMapStore());
            services.AddSingleton<IModelProviderMapLeaseCoordinator>(
                new ModelProviderMapLeaseCoordinator(new KeyedCompositeLockDomain()));
            services.AddSingleton(Substitute.For<ILocalModelProviderResolver>());
            services.AddSingleton(Substitute.For<ILocalChatClientCacheInvalidator>());
            services.AddScoped<IExternalProviderReconciler, ExternalProviderReconciler>();
            services.AddSingleton<ILogger<ExternalProviderReconciler>>(NullLogger<ExternalProviderReconciler>.Instance);
        }

        return services.BuildServiceProvider();
    }

    private static string NewRoot(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        return root;
    }
}
