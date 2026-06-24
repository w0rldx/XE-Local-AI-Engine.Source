namespace XE_Local_AI_Engine.Tests.ModelFit;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

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
        return new LlamaCppUpdateCheckService(settings, catalog, installedStore, state,
            NullLogger<LlamaCppUpdateCheckService>.Instance, TimeSpan.Zero);
    }
}
