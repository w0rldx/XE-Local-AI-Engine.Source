namespace XE_Local_AI_Engine.Tests.ModelFit;

using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

[Category(TestCategories.Unit)]
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

        var result = await service.StartAcquisitionAsync(GpuVariant.Cpu);

        AssertEx.True(result.Accepted);
        AssertEx.True(calls.SequenceEqual(["lease", "ensure"], StringComparer.Ordinal),
            "the mutation lease must be held before the acquisition task is accepted.");
        completion.SetResult(new LlamaBinary
        {
            ServerExecutablePath = "/tmp/llama-server",
            Version = "b1",
            Variant = GpuVariant.Cpu,
            IsPinnedFallback = true
        });
        await lease.Disposed;
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

        var result = await service.StartAcquisitionAsync(GpuVariant.Cpu);

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

        var result = await service.StartAcquisitionAsync(GpuVariant.Cpu);

        AssertEx.False(result.Accepted);
        AssertEx.Contains(result.DisplayMessage!, "source-built", StringComparison.Ordinal);
        await lease.Disposed;
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

        var result = await service.StartAcquisitionAsync(GpuVariant.Cpu);

        AssertEx.False(result.Accepted);
        AssertEx.Contains(result.DisplayMessage!, "active", StringComparison.Ordinal);
        await lease.Disposed;
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

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => service.StartAcquisitionAsync(GpuVariant.Cpu));

        await lease.Disposed;
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

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => service.StartAcquisitionAsync(GpuVariant.Cpu));

        await lease.Disposed;
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

        var result = await service.InstallAsync("b1");

        AssertEx.False(result.Succeeded);
        AssertEx.Equal(LlamaCppRuntimeAdministrationFailure.Busy, result.Failure);
        await variantSelector.DidNotReceive().SelectVariantAsync(Arg.Any<CancellationToken>());
        await releaseCatalog.DidNotReceiveWithAnyArgs()
                            .ResolveAssetAsync(default!, default, default, default, default);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task InstallAsync_LinuxCuda_RefusesWithTheEnsureMessageBeforeCatalogOrLease()
    {
        var releaseCatalog = Substitute.For<ILlamaCppReleaseCatalog>();
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        var service = CreateService(binaryManager, supervisor, releaseCatalog: releaseCatalog);

        var result = await service.InstallAsync("b10201", GpuVariant.Cuda);

        AssertEx.False(result.Succeeded);
        AssertEx.Equal(LlamaCppRuntimeAdministrationFailure.RuntimeFailure, result.Failure);
        AssertEx.Equal(LlamaCppReleasePins.MissingPrebuiltMessage(OSPlatform.Linux, RuntimeInformation.OSArchitecture, GpuVariant.Cuda), result.DisplayMessage);
        await releaseCatalog.DidNotReceiveWithAnyArgs().ResolveAssetAsync(default!, default, default, default, default);
        await supervisor.DidNotReceiveWithAnyArgs().TryAcquireRuntimeMutationLeaseAsync(default);
        await binaryManager.DidNotReceiveWithAnyArgs().InstallTagAsync(default!, default!, default!, default, default, default!, default);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task InstallAsync_LinuxCudaWithInstalledSourceBuild_AnswersTheSourceBuildRefusalNotTheMissingPrebuilt()
    {
        var installedStore = Substitute.For<IInstalledRuntimeStore>();
        installedStore.ReadAsync(Arg.Any<CancellationToken>())
                      .Returns(new InstalledRuntimeState("b10201", "(source-build:cuda)", new string('a', 64), GpuVariant.Cuda, DateTimeOffset.UtcNow, "/cache/llama.cpp/source-cuda/bin"));
        var releaseCatalog = Substitute.For<ILlamaCppReleaseCatalog>();
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        var service = CreateService(binaryManager, Substitute.For<ILlamaServerProcessSupervisor>(), installedStore, releaseCatalog: releaseCatalog);

        var result = await service.InstallAsync("b10201", GpuVariant.Cuda);

        AssertEx.False(result.Succeeded);
        AssertEx.Equal(LlamaCppRuntimeAdministrationFailure.Busy, result.Failure);
        AssertEx.Equal("Remove the installed source-built llama.cpp runtime before installing a prebuilt runtime.", result.DisplayMessage);
        await releaseCatalog.DidNotReceiveWithAnyArgs().ResolveAssetAsync(default!, default, default, default, default);
        await binaryManager.DidNotReceiveWithAnyArgs().InstallTagAsync(default!, default!, default!, default, default, default!, default);
    }

    [Test]
    public async Task EnsureAsync_OfTheInstalledSourceBuildVariant_ServesTheManagedBuildWithoutTheLease()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.CountRunningProcesses().Returns(0);
        var installedStore = Substitute.For<IInstalledRuntimeStore>();
        installedStore.ReadAsync(Arg.Any<CancellationToken>())
                      .Returns(new InstalledRuntimeState("b10201", "(source-build:cuda)", new string('a', 64), GpuVariant.Cuda, DateTimeOffset.UtcNow, "/cache/llama.cpp/source-cuda/bin"));
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        binaryManager.EnsureBinaryAsync(GpuVariant.Cuda, Arg.Any<CancellationToken>())
                     .Returns(new LlamaBinary
                     {
                         ServerExecutablePath = "/cache/llama.cpp/source-cuda/bin/llama-server",
                         Version = "b10201",
                         Variant = GpuVariant.Cuda,
                         IsPinnedFallback = false
                     });
        var service = CreateService(binaryManager, supervisor, installedStore);

        var result = await service.EnsureAsync(GpuVariant.Cuda);

        AssertEx.True(result.Succeeded, result.DisplayMessage);
        AssertEx.Equal("cuda", result.Binary!.Variant);
        await supervisor.DidNotReceiveWithAnyArgs().TryAcquireRuntimeMutationLeaseAsync(default);
    }

    [Test]
    public async Task EnsureAsync_OfAnotherVariantThanTheInstalledSourceBuild_StillAnswersTheSourceBuildRefusal()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.CountRunningProcesses().Returns(0);
        var installedStore = Substitute.For<IInstalledRuntimeStore>();
        installedStore.ReadAsync(Arg.Any<CancellationToken>())
                      .Returns(new InstalledRuntimeState("b10201", "(source-build:cuda)", new string('a', 64), GpuVariant.Cuda, DateTimeOffset.UtcNow, "/cache/llama.cpp/source-cuda/bin"));
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        var service = CreateService(binaryManager, supervisor, installedStore);

        var result = await service.EnsureAsync(GpuVariant.Vulkan);

        AssertEx.Equal(LlamaCppRuntimeAdministrationFailure.Busy, result.Failure);
        AssertEx.Equal("Remove the installed source-built llama.cpp runtime before installing a prebuilt runtime.", result.DisplayMessage);
        await binaryManager.DidNotReceiveWithAnyArgs().EnsureBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnsureAsync_WhileALlamaServerRuns_ReachesTheBinaryManagerWithoutTheLease()
    {
        // The real supervisor refuses the lease while any llama-server runs; the ensure must not depend on it.
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<ILlamaServerRuntimeMutationLease?>(null));
        supervisor.CountRunningProcesses().Returns(1);
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        binaryManager.EnsureBinaryAsync(GpuVariant.Cuda, Arg.Any<CancellationToken>())
                     .Returns(new LlamaBinary
                     {
                         ServerExecutablePath = "/cache/llama.cpp/cuda/llama-server",
                         Version = "b1",
                         Variant = GpuVariant.Cuda,
                         IsPinnedFallback = true
                     });
        var service = CreateService(binaryManager, supervisor);

        var result = await service.EnsureAsync(GpuVariant.Cuda);

        AssertEx.True(result.Succeeded, result.DisplayMessage);
        await binaryManager.Received(1).EnsureBinaryAsync(GpuVariant.Cuda, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnsureAsync_WaitsForTheSharedRuntimeEntry_AndHoldsItAcrossTheBinaryEnsureOnly()
    {
        // The entry stays pending while an install holds the exclusive lease; ensure must not touch the binary manager
        // (whose RecordResolvedRuntimeAsync would overwrite the installed record) until it is granted.
        var steps = new List<string>();
        var entry = Substitute.For<IDisposable>();
        entry.When(static disposable => disposable.Dispose()).Do(_ => steps.Add("exit"));
        var granted = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.EnterRuntimeMutationSharedAsync(Arg.Any<CancellationToken>()).Returns(granted.Task);
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        binaryManager.EnsureBinaryAsync(GpuVariant.Cpu, Arg.Any<CancellationToken>())
                     .Returns(_ =>
                     {
                         steps.Add("ensure");
                         return new LlamaBinary
                         {
                             ServerExecutablePath = "/cache/llama.cpp/cpu/llama-server",
                             Version = "b1",
                             Variant = GpuVariant.Cpu,
                             IsPinnedFallback = true
                         };
                     });
        var service = CreateService(binaryManager, supervisor);

        var ensuring = service.EnsureAsync(GpuVariant.Cpu);
        await AssertEx.StaysIncompleteAsync(ensuring, "Ensure must wait for the shared runtime entry.");
        AssertEx.Empty(steps);

        granted.SetResult(entry);
        var result = await ensuring.WaitAsync(TimeSpan.FromSeconds(3));

        AssertEx.True(result.Succeeded, result.DisplayMessage);
        AssertEx.True(steps.SequenceEqual(["ensure", "exit"]), $"Expected ensure then exit, saw {string.Join(", ", steps)}.");
        await supervisor.DidNotReceiveWithAnyArgs().TryAcquireRuntimeMutationLeaseAsync(default);
    }

    [Test]
    public async Task EnsureAsync_WithKeepModelWarmOn_ReachesTheBinaryManager_WhileInstallIsStillRefused()
    {
        // A warm CPU-fallback model is exactly the case a CUDA repair exists for: ensure only lands missing files and
        // stops no server, so Keep Model Warm must not block it. Install swaps the tag and stops servers, so it still does.
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();
        runtimeSettings.GetKeepModelWarmEnabledAsync(Arg.Any<CancellationToken>()).Returns(true);
        runtimeSettings.GetRecommendedLlamaCppTagAsync(Arg.Any<CancellationToken>()).Returns("b1");
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        binaryManager.EnsureBinaryAsync(GpuVariant.Cuda, Arg.Any<CancellationToken>())
                     .Returns(new LlamaBinary
                     {
                         ServerExecutablePath = "/cache/llama.cpp/cuda/llama-server",
                         Version = "b1",
                         Variant = GpuVariant.Cuda,
                         IsPinnedFallback = true
                     });
        var service = CreateService(binaryManager, Substitute.For<ILlamaServerProcessSupervisor>(), runtimeSettings: runtimeSettings);

        var ensured = await service.EnsureAsync(GpuVariant.Cuda);
        var installed = await service.InstallAsync("b1");

        AssertEx.True(ensured.Succeeded, ensured.DisplayMessage);
        await binaryManager.Received(1).EnsureBinaryAsync(GpuVariant.Cuda, Arg.Any<CancellationToken>());
        AssertEx.False(installed.Succeeded, "Install still waits for Keep Model Warm to be off.");
        AssertEx.Equal(LlamaCppRuntimeAdministrationFailure.Busy, installed.Failure);
    }

    [Test]
    public async Task InstallAsync_WhileALlamaServerRuns_IsRefusedWithTheEjectMessage()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<ILlamaServerRuntimeMutationLease?>(null));
        supervisor.CountRunningProcesses().Returns(1);
        var releaseCatalog = Substitute.For<ILlamaCppReleaseCatalog>();
        releaseCatalog.ResolveAssetAsync("b1", Arg.Any<OSPlatform>(), Arg.Any<Architecture>(), GpuVariant.Cpu, Arg.Any<CancellationToken>())
                      .Returns(LlamaCppReleaseResult.ForAsset("b1", new LlamaCppReleaseAsset
                      {
                          Name = "llama-b1-bin-cpu.zip",
                          DownloadUrl = new Uri("https://example.invalid/llama-b1-bin-cpu.zip"),
                          Digest = new string('a', 64),
                          Size = 1
                      }));
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        var service = CreateService(binaryManager, supervisor, releaseCatalog: releaseCatalog);

        var result = await service.InstallAsync("b1", GpuVariant.Cpu);

        AssertEx.Equal(LlamaCppRuntimeAdministrationFailure.Busy, result.Failure);
        AssertEx.Equal("Stop or eject all running llama.cpp models before updating the runtime.", result.DisplayMessage);
        AssertEx.Equal(1, result.RunningProcessCount);
        await binaryManager.DidNotReceiveWithAnyArgs().InstallTagAsync(default!, default!, default!, default, default, default!, default);
    }

    [Test]
    public async Task GetStatusAsync_ReportsTheOverrideVariantOnlyWhileAnOverrideIsSet()
    {
        var withOverride = CreateService(Substitute.For<ILlamaCppBinaryManager>(),
            Substitute.For<ILlamaServerProcessSupervisor>(),
            overrideOptions: new LlamaServerRuntimeOverrideOptions
            {
                ServerPath = "/opt/llama/build/bin/llama-server",
                Variant = GpuVariant.Cuda
            });
        var withoutOverride = CreateService(Substitute.For<ILlamaCppBinaryManager>(), Substitute.For<ILlamaServerProcessSupervisor>());

        AssertEx.Equal(GpuVariant.Cuda, (await withOverride.GetStatusAsync()).OverrideVariant);
        AssertEx.Null((await withoutOverride.GetStatusAsync()).OverrideVariant);
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
        string[] forbidden = ["ServerExecutablePath", "SourceBuildPath", "ServerPath", "Sha256"];

        foreach (var view in publicViews)
        {
            var names = view.GetProperties().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
            AssertEx.False(names.Overlaps(forbidden), $"{view.Name} must not expose runtime paths or hashes.");
        }

        var json = JsonSerializer.Serialize(new LlamaCppInstalledRuntimeView
        {
            Tag = "b1",
            Asset = "asset.zip",
            Variant = "cpu",
            InstalledAtUnixTimeMilliseconds = 1,
            IsSourceBuild = true,
            SourceRepository = "repository",
            SourceCommit = "commit",
            SourceRevisionMode = 0,
            SourceRequestedCommit = "requested",
            SourceSelection = 0
        });
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
        IGpuVariantSelector? variantSelector = null,
        LlamaServerRuntimeOverrideOptions? overrideOptions = null)
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
        acquisition.Current.Returns(new RuntimeAcquisitionStatusEvent
        {
            Sequence = 0,
            Phase = "Idle",
            Variant = null,
            Tag = null,
            CompletedBytes = null,
            TotalBytes = null,
            StepIndex = 1,
            StepCount = 1,
            SanitizedError = null
        });

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
            overrideOptions ?? new LlamaServerRuntimeOverrideOptions(),
            lifetime,
            NullLogger<LlamaCppRuntimeAdministrationService>.Instance,
            TimeProvider.System,
            Substitute.For<IRuntimeDeviceAudit>());
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
