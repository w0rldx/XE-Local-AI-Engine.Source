namespace XE_Local_AI_Engine.Tests.ModelFit;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     Unit tests for the one-shot startup update check (<see cref="LlamaCppUpdateCheckService" />). Drives the check
///     directly (no startup delay) and asserts it sets <c>updateAvailable</c> only when a NEWER recommended tag is
///     resolvable, and degrades to an <c>isOffline</c> snapshot — never throwing — when the catalog has no live data.
/// </summary>
public sealed class LlamaCppUpdateCheckServiceTests
{
    [Test]
    public async Task CheckOnce_WhenRecommendedDiffersFromInstalled_RaisesUpdateAvailable()
    {
        var state = new LlamaCppUpdateState();
        var catalog = Substitute.For<ILlamaCppReleaseCatalog>();
        catalog.ResolveRecommendedAsync("b9700", Arg.Any<CancellationToken>())
               .Returns(LlamaCppReleaseResult.ForTag("b9700"));
        StubUpstream(catalog, "b9777");
        var installedStore = Substitute.For<IInstalledRuntimeStore>();
        installedStore.ReadAsync(Arg.Any<CancellationToken>())
                      .Returns(new InstalledRuntimeState("b9692", "asset.tar.gz", "deadbeef", GpuVariant.Cpu, DateTimeOffset.UtcNow));
        using var service = CreateService(catalog, installedStore, state, recommendedTag: "b9700");

        await service.CheckOnceAsync(CancellationToken.None);

        var snapshot = state.Current;
        AssertEx.True(snapshot.UpdateAvailable, "A newer recommended tag must raise updateAvailable.");
        AssertEx.Equal("b9700", snapshot.RecommendedTag);
        AssertEx.Equal("b9692", snapshot.InstalledTag);
        AssertEx.False(snapshot.IsOffline, "A live resolution is not offline.");
    }

    [Test]
    public async Task CheckOnce_ResolvesUpstreamLatestTagOntoSnapshot()
    {
        // The startup check must populate upstreamLatestTag so developer mode has it on the mount GET — no ?refresh.
        var state = new LlamaCppUpdateState();
        var catalog = Substitute.For<ILlamaCppReleaseCatalog>();
        catalog.ResolveRecommendedAsync("b9700", Arg.Any<CancellationToken>())
               .Returns(LlamaCppReleaseResult.ForTag("b9700"));
        StubUpstream(catalog, "b9999");
        var installedStore = Substitute.For<IInstalledRuntimeStore>();
        installedStore.ReadAsync(Arg.Any<CancellationToken>())
                      .Returns(new InstalledRuntimeState("b9692", "asset.tar.gz", "deadbeef", GpuVariant.Cpu, DateTimeOffset.UtcNow));
        using var service = CreateService(catalog, installedStore, state, recommendedTag: "b9700");

        await service.CheckOnceAsync(CancellationToken.None);

        AssertEx.Equal("b9999", state.Current.UpstreamLatestTag);
    }

    [Test]
    public async Task CheckOnce_WhenUpstreamOffline_LeavesUpstreamNullWithoutThrowing()
    {
        // An unreachable upstream-latest lookup must degrade to a null upstream tag, never throw, even when the
        // recommended resolution succeeded.
        var state = new LlamaCppUpdateState();
        var catalog = Substitute.For<ILlamaCppReleaseCatalog>();
        catalog.ResolveRecommendedAsync("b9700", Arg.Any<CancellationToken>())
               .Returns(LlamaCppReleaseResult.ForTag("b9700"));
        catalog.ResolveUpstreamLatestAsync(Arg.Any<CancellationToken>()).Returns(LlamaCppReleaseResult.Offline());
        var installedStore = Substitute.For<IInstalledRuntimeStore>();
        installedStore.ReadAsync(Arg.Any<CancellationToken>())
                      .Returns(new InstalledRuntimeState("b9692", "asset.tar.gz", "deadbeef", GpuVariant.Cpu, DateTimeOffset.UtcNow));
        using var service = CreateService(catalog, installedStore, state, recommendedTag: "b9700");

        await service.CheckOnceAsync(CancellationToken.None);

        AssertEx.Null(state.Current.UpstreamLatestTag);
        AssertEx.True(state.Current.UpdateAvailable, "A resolvable recommended tag still advertises an update when only upstream is offline.");
    }

    [Test]
    public async Task CheckOnce_WhenRecommendedEqualsInstalled_DoesNotRaiseUpdateAvailable()
    {
        var state = new LlamaCppUpdateState();
        var catalog = Substitute.For<ILlamaCppReleaseCatalog>();
        catalog.ResolveRecommendedAsync("b9692", Arg.Any<CancellationToken>())
               .Returns(LlamaCppReleaseResult.ForTag("b9692"));
        StubUpstream(catalog, "b9777");
        var installedStore = Substitute.For<IInstalledRuntimeStore>();
        installedStore.ReadAsync(Arg.Any<CancellationToken>())
                      .Returns(new InstalledRuntimeState("b9692", "asset.tar.gz", "deadbeef", GpuVariant.Cpu, DateTimeOffset.UtcNow));
        using var service = CreateService(catalog, installedStore, state, recommendedTag: "b9692");

        await service.CheckOnceAsync(CancellationToken.None);

        AssertEx.False(state.Current.UpdateAvailable, "Recommended == installed must not advertise an update.");
    }

    [Test]
    public async Task CheckOnce_WhenCatalogOffline_RecordsOfflineWithoutUpdate()
    {
        var state = new LlamaCppUpdateState();
        var catalog = Substitute.For<ILlamaCppReleaseCatalog>();
        catalog.ResolveRecommendedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(LlamaCppReleaseResult.Offline());
        catalog.ResolveUpstreamLatestAsync(Arg.Any<CancellationToken>()).Returns(LlamaCppReleaseResult.Offline());
        var installedStore = Substitute.For<IInstalledRuntimeStore>();
        installedStore.ReadAsync(Arg.Any<CancellationToken>())
                      .Returns(new InstalledRuntimeState("b9692", "asset.tar.gz", "deadbeef", GpuVariant.Cpu, DateTimeOffset.UtcNow));
        using var service = CreateService(catalog, installedStore, state, recommendedTag: "b9700");

        await service.CheckOnceAsync(CancellationToken.None);

        var snapshot = state.Current;
        AssertEx.True(snapshot.IsOffline, "An offline catalog must produce an isOffline snapshot.");
        AssertEx.False(snapshot.UpdateAvailable, "Offline must not advertise an update.");
    }

    [Test]
    public async Task CheckOnce_WhenNoInstalledState_RaisesUpdateAvailableForFreshNode()
    {
        var state = new LlamaCppUpdateState();
        var catalog = Substitute.For<ILlamaCppReleaseCatalog>();
        catalog.ResolveRecommendedAsync("b9692", Arg.Any<CancellationToken>())
               .Returns(LlamaCppReleaseResult.ForTag("b9692"));
        StubUpstream(catalog, "b9777");
        var installedStore = Substitute.For<IInstalledRuntimeStore>();
        installedStore.ReadAsync(Arg.Any<CancellationToken>()).Returns((InstalledRuntimeState?)null);
        using var service = CreateService(catalog, installedStore, state, recommendedTag: "b9692");

        await service.CheckOnceAsync(CancellationToken.None);

        var snapshot = state.Current;
        AssertEx.True(snapshot.UpdateAvailable, "A fresh node (no install record) must offer the recommended install.");
        AssertEx.Null(snapshot.InstalledTag);
    }

    // Brief §5 test 1 for the runtime check. Asserting the SNAPSHOT (still empty) as well as the catalog (never called)
    // is the point: a gated-off node must not report as "checked, nothing found", which is what the panel renders green.
    [Test]
    public async Task Execute_WhenRuntimeUpdateChecksAreDisabled_StoresNoSnapshot()
    {
        var state = new LlamaCppUpdateState();
        var catalog = Substitute.For<ILlamaCppReleaseCatalog>();
        var runtimeSettings = StubNodeRuntimeSettings.Create()
                                                     .WithExternalAccessProfile(StoredNodeSettings.ExternalAccessProfileOffline)
                                                     .WithAutoCheckRuntimeUpdates(false)
                                                     .Build();
        using var service = CreateService(catalog, Substitute.For<IInstalledRuntimeStore>(), state, runtimeSettings, new ManualTimeProvider());

        await BackgroundServiceTestHelper.RunExecuteAsync(service, CancellationToken.None);

        AssertEx.Empty(catalog.ReceivedCalls(), "A disabled runtime-update check must never reach the release catalog.");
        AssertEx.Null(state.Current.CheckedAtUtc, "The snapshot must stay unchecked, not report a check that never ran.");
        AssertEx.False(state.Current.UpdateAvailable);
    }

    // Brief §5 test 2 for the runtime check, on the fake clock: no catalog call while undecided, one once decided.
    [Test]
    public async Task Execute_WhileTheProfileIsUndecided_Waits_ThenChecksOnceItIsDecided()
    {
        var state = new LlamaCppUpdateState();
        var catalog = Substitute.For<ILlamaCppReleaseCatalog>();
        catalog.ResolveRecommendedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(LlamaCppReleaseResult.ForTag("b9700"));
        StubUpstream(catalog, "b9700");

        var decided = false;
        var runtimeSettings = StubNodeRuntimeSettings.Create()
                                                     .WithExternalAccessProfileRead(_ => Task.FromResult<string?>(decided
                                                         ? StoredNodeSettings.ExternalAccessProfileRecommended
                                                         : null))
                                                     .Build();
        var timeProvider = new ManualTimeProvider();
        var service = CreateService(catalog, Substitute.For<IInstalledRuntimeStore>(), state, runtimeSettings, timeProvider);
        try
        {
            var run = BackgroundServiceTestHelper.RunExecuteAsync(service, CancellationToken.None);

            await AssertEx.EventuallyAsync(() => timeProvider.ArmedTimerCount > 0,
                TimeSpan.FromSeconds(5),
                "The gate must arm its poll timer while the profile is undecided.");
            await AssertEx.SettleAsync();
            AssertEx.Empty(catalog.ReceivedCalls(), "Nothing may be fetched before the operator has chosen.");

            decided = true;
            timeProvider.Advance(ExternalAccessGate.PollInterval);

            await AssertEx.CompletesAsync(run, TimeSpan.FromSeconds(5), "ExecuteAsync must finish after the one-shot check.");
            AssertEx.True(state.Current.CheckedAtUtc is not null,
                "The check must have run and stamped the snapshot once the profile was decided.");
        }
        finally
        {
            service.Dispose();
        }
    }

    private static void StubUpstream(ILlamaCppReleaseCatalog catalog, string upstreamTag)
    {
        catalog.ResolveUpstreamLatestAsync(Arg.Any<CancellationToken>()).Returns(LlamaCppReleaseResult.ForTag(upstreamTag));
    }

    private static LlamaCppUpdateCheckService CreateService(ILlamaCppReleaseCatalog catalog,
        IInstalledRuntimeStore installedStore,
        ILlamaCppUpdateState state,
        string recommendedTag)
    {
        var settings = Substitute.For<INodeRuntimeSettings>();
        settings.GetRecommendedLlamaCppTagAsync(Arg.Any<CancellationToken>()).Returns(recommendedTag);
        settings.GetExternalAccessProfileAsync(Arg.Any<CancellationToken>())
                .Returns(StoredNodeSettings.ExternalAccessProfileRecommended);
        settings.GetAutoCheckRuntimeUpdatesAsync(Arg.Any<CancellationToken>()).Returns(true);
        return CreateService(catalog, installedStore, state, settings, TimeProvider.System);
    }

    private static LlamaCppUpdateCheckService CreateService(ILlamaCppReleaseCatalog catalog,
        IInstalledRuntimeStore installedStore,
        ILlamaCppUpdateState state,
        INodeRuntimeSettings runtimeSettings,
        TimeProvider timeProvider)
    {
        return new LlamaCppUpdateCheckService(runtimeSettings,
            catalog,
            installedStore,
            state,
            timeProvider,
            NullLogger<LlamaCppUpdateCheckService>.Instance,
            TimeSpan.Zero);
    }
}
