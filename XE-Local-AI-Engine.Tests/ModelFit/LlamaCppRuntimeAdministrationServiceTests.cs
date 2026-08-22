namespace XE_Local_AI_Engine.Tests.ModelFit;

using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Configuration;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LlamaCppRuntimeAdministrationServiceTests
{
    [Test]
    public async Task StartAcquisitionAsync_AcquiresMutationLeaseBeforeAcceptingAndOwnsDetachedTask()
    {
        var calls = new List<string>();
#pragma warning disable CA2000 // Ownership transfers to the accepted acquisition task, which the test awaits through Disposed.
        var lease = new RecordingLease();
#pragma warning restore CA2000
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>())
                  .Returns(_ =>
                  {
                      calls.Add("lease");
                      return Task.FromResult<ILlamaServerRuntimeMutationLease?>(lease);
                  });
        supervisor.CountRunningProcesses().Returns(0);
        var completion = new TaskCompletionSource<LlamaBinary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        binaryManager.EnsureBinaryAsync(GpuVariant.Cpu, lease, Arg.Any<CancellationToken>())
                     .Returns(_ =>
                     {
                         calls.Add("ensure");
                         return completion.Task;
                     });
        var service = CreateService(binaryManager, supervisor);

        var result = await service.StartAcquisitionAsync(GpuVariant.Cpu).ConfigureAwait(false);

        AssertEx.True(result.Accepted);
        AssertEx.True(calls.SequenceEqual(["lease", "ensure"], StringComparer.Ordinal),
            "the mutation lease must be held before the acquisition task is accepted.");
        completion.SetResult(new LlamaBinary("/tmp/llama-server", "b1", GpuVariant.Cpu, true));
        await lease.Disposed.ConfigureAwait(false);
        AssertEx.Equal(1, lease.DisposeCount);
    }

    [Test]
    public async Task StartAcquisitionAsync_WhenLeaseUnavailable_ReturnsBusyWithoutStartingBinaryWork()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<ILlamaServerRuntimeMutationLease?>(null));
        supervisor.CountRunningProcesses().Returns(1);
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        var service = CreateService(binaryManager, supervisor);

        var result = await service.StartAcquisitionAsync(GpuVariant.Cpu).ConfigureAwait(false);

        AssertEx.False(result.Accepted);
        AssertEx.Equal(1, result.RunningProcessCount);
        await binaryManager.DidNotReceiveWithAnyArgs()
                           .EnsureBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<ILlamaServerRuntimeMutationLease>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartAcquisitionAsync_WhenSourceRuntimeIsInstalled_RejectsAndDisposesLease()
    {
#pragma warning disable CA2000 // Ownership transfers to the administration service, which disposes the rejected admission lease.
        var lease = new RecordingLease();
#pragma warning restore CA2000
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<ILlamaServerRuntimeMutationLease?>(lease));
        supervisor.CountRunningProcesses().Returns(0);
        var installedStore = Substitute.For<IInstalledRuntimeStore>();
        installedStore.ReadAsync(Arg.Any<CancellationToken>())
                      .Returns(new InstalledRuntimeState("b1", "source", "sha", GpuVariant.Cpu, DateTimeOffset.UtcNow, "/managed/source"));
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        var service = CreateService(binaryManager, supervisor, installedStore: installedStore);

        var result = await service.StartAcquisitionAsync(GpuVariant.Cpu).ConfigureAwait(false);

        AssertEx.False(result.Accepted);
        AssertEx.Contains(result.DisplayMessage!, "source-built", StringComparison.Ordinal);
        await lease.Disposed.ConfigureAwait(false);
        await binaryManager.DidNotReceiveWithAnyArgs()
                           .EnsureBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<ILlamaServerRuntimeMutationLease>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartAcquisitionAsync_WhenSourceBuildIsActive_RejectsAndDisposesLease()
    {
#pragma warning disable CA2000 // Ownership transfers to the administration service, which disposes the rejected admission lease.
        var lease = new RecordingLease();
#pragma warning restore CA2000
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<ILlamaServerRuntimeMutationLease?>(lease));
        supervisor.CountRunningProcesses().Returns(0);
        var sourceBuildActivity = Substitute.For<ILlamaCppSourceBuildActivity>();
        sourceBuildActivity.ActiveBuildId.Returns(Guid.NewGuid());
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        var service = CreateService(binaryManager, supervisor, sourceBuildActivity: sourceBuildActivity);

        var result = await service.StartAcquisitionAsync(GpuVariant.Cpu).ConfigureAwait(false);

        AssertEx.False(result.Accepted);
        AssertEx.Contains(result.DisplayMessage!, "active", StringComparison.Ordinal);
        await lease.Disposed.ConfigureAwait(false);
        await binaryManager.DidNotReceiveWithAnyArgs()
                           .EnsureBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<ILlamaServerRuntimeMutationLease>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartAcquisitionAsync_WhenPostLeaseProbeIsCancelled_DisposesLeaseAndPropagates()
    {
#pragma warning disable CA2000 // Ownership transfers to the administration service, which must dispose on cancellation.
        var lease = new RecordingLease();
#pragma warning restore CA2000
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<ILlamaServerRuntimeMutationLease?>(lease));
        var installedStore = Substitute.For<IInstalledRuntimeStore>();
        installedStore.ReadAsync(Arg.Any<CancellationToken>()).Returns<Task<InstalledRuntimeState?>>(_ => throw new OperationCanceledException());
        var service = CreateService(Substitute.For<ILlamaCppBinaryManager>(), supervisor, installedStore: installedStore);

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => service.StartAcquisitionAsync(GpuVariant.Cpu)).ConfigureAwait(false);

        await lease.Disposed.ConfigureAwait(false);
        AssertEx.Equal(1, lease.DisposeCount);
    }

    [Test]
    public async Task StartAcquisitionAsync_WhenPostLeaseProbeThrows_DisposesLeaseAndPropagates()
    {
#pragma warning disable CA2000 // Ownership transfers to the administration service, which must dispose on failure.
        var lease = new RecordingLease();
#pragma warning restore CA2000
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<ILlamaServerRuntimeMutationLease?>(lease));
        var installedStore = Substitute.For<IInstalledRuntimeStore>();
        installedStore.ReadAsync(Arg.Any<CancellationToken>()).Returns<Task<InstalledRuntimeState?>>(_ => throw new InvalidOperationException("probe failed"));
        var service = CreateService(Substitute.For<ILlamaCppBinaryManager>(), supervisor, installedStore: installedStore);

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => service.StartAcquisitionAsync(GpuVariant.Cpu)).ConfigureAwait(false);

        await lease.Disposed.ConfigureAwait(false);
        AssertEx.Equal(1, lease.DisposeCount);
    }

    [Test]
    public async Task InstallAsync_WhenKeepWarmEnabled_RejectsBeforeVariantSelectionOrCatalogLookup()
    {
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();
        runtimeSettings.GetKeepModelWarmEnabledAsync(Arg.Any<CancellationToken>()).Returns(true);
        var releaseCatalog = Substitute.For<ILlamaCppReleaseCatalog>();
        var variantSelector = Substitute.For<IGpuVariantSelector>();
        var service = CreateService(Substitute.For<ILlamaCppBinaryManager>(),
            Substitute.For<ILlamaServerProcessSupervisor>(),
            runtimeSettings: runtimeSettings,
            releaseCatalog: releaseCatalog,
            variantSelector: variantSelector);

        var result = await service.InstallAsync("b1").ConfigureAwait(false);

        AssertEx.False(result.Succeeded);
        AssertEx.Equal(LlamaCppRuntimeAdministrationFailure.Busy, result.Failure);
        await variantSelector.DidNotReceive().SelectVariantAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await releaseCatalog.DidNotReceiveWithAnyArgs()
                            .ResolveAssetAsync(default!, default, default, default, default).ConfigureAwait(false);
    }

    [Test]
    public void RuntimeAdministrationViews_DoNotExposeProviderPathsOrHashes()
    {
        Type[] publicViews =
        [
            typeof(LlamaCppRuntimeStatus),
            typeof(LlamaCppRuntimeBinaryView),
            typeof(LlamaCppInstalledRuntimeView),
            typeof(LlamaCppRuntimeMutationResult)
        ];
        string[] forbidden = ["ServerExecutablePath", "SourceBuildPath", "Sha256"];

        foreach (var view in publicViews)
        {
            var names = view.GetProperties().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
            AssertEx.False(names.Overlaps(forbidden), $"{view.Name} must not expose runtime paths or hashes.");
        }

        var json = JsonSerializer.Serialize(new LlamaCppInstalledRuntimeView("b1",
            "asset.zip",
            "cpu",
            1,
            true,
            "repository",
            "commit",
            0,
            "requested",
            0));
        foreach (var forbiddenName in forbidden)
        {
            AssertEx.False(json.Contains(forbiddenName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static LlamaCppRuntimeAdministrationService CreateService(ILlamaCppBinaryManager binaryManager,
        ILlamaServerProcessSupervisor supervisor,
        IInstalledRuntimeStore? installedStore = null,
        ILlamaCppSourceBuildActivity? sourceBuildActivity = null,
        INodeRuntimeSettings? runtimeSettings = null,
        ILlamaCppReleaseCatalog? releaseCatalog = null,
        IGpuVariantSelector? variantSelector = null)
    {
        if (runtimeSettings is null)
        {
            runtimeSettings = Substitute.For<INodeRuntimeSettings>();
            runtimeSettings.GetKeepModelWarmEnabledAsync(Arg.Any<CancellationToken>()).Returns(false);
            runtimeSettings.GetRecommendedLlamaCppTagAsync(Arg.Any<CancellationToken>()).Returns("b1");
        }
        if (installedStore is null)
        {
            installedStore = Substitute.For<IInstalledRuntimeStore>();
            installedStore.ReadAsync(Arg.Any<CancellationToken>()).Returns((InstalledRuntimeState?)null);
        }
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        var acquisition = Substitute.For<IRuntimeAcquisitionStatusRegistry>();
        acquisition.Current.Returns(new RuntimeAcquisitionStatusHubEvent(0, "Idle", null, null, null, null, 1, 1, null));

        return new LlamaCppRuntimeAdministrationService(binaryManager,
            releaseCatalog ?? Substitute.For<ILlamaCppReleaseCatalog>(),
            variantSelector ?? Substitute.For<IGpuVariantSelector>(),
            installedStore,
            sourceBuildActivity ?? Substitute.For<ILlamaCppSourceBuildActivity>(),
            new LlamaCppUpdateState(),
            acquisition,
            runtimeSettings,
            supervisor,
            Substitute.For<ILocalChatClientCacheInvalidator>(),
            new LlamaServerRuntimeOverrideOptions(),
            lifetime,
            NullLogger<LlamaCppRuntimeAdministrationService>.Instance);
    }

    private sealed class RecordingLease : ILlamaServerRuntimeMutationLease
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount { get; private set; }
        public Task Disposed => _disposed.Task;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
