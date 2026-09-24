namespace XE_Local_AI_Engine.Tests.Providers;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Compute.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.Training.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
[NotInParallel]
public sealed class RuntimeCacheDirectoryTests
{
    [Test]
    public void Unset_PreservesDefault()
    {
        AssertEx.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XE-Local-AI-Engine"),
            RuntimeCacheDirectory.Resolve(null));
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("relative/cache")]
    [Arguments("/tmp/cache\n")]
    public void InvalidOverride_FailsClosed(string value)
    {
        AssertEx.Throws<InvalidOperationException>(() => RuntimeCacheDirectory.Resolve(value));
    }

    [Test]
    public void AbsoluteOverride_IsNormalized()
    {
        var root = Path.Combine(Path.GetTempPath(), "runtime-cache");
        AssertEx.Equal(root, RuntimeCacheDirectory.Resolve(Path.Combine(root, "child", "..") + Path.DirectorySeparatorChar));
    }

    [Test]
    public async Task EnvironmentOverride_RoutesStoresReapersAndRecoveryToIsolatedRoot()
    {
        var root = Directory.CreateTempSubdirectory("xe-runtime-root-").FullName;
        var original = Environment.GetEnvironmentVariable(RuntimeCacheDirectory.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(RuntimeCacheDirectory.EnvironmentVariable, root);
            AssertEx.Equal(root, RuntimeCacheDirectory.Resolve());
            AssertEx.Equal(Path.Combine(root, "training-runtime"), TrainingRuntimeLayout.DefaultCacheRoot());
            AssertEx.Equal(Path.Combine(root, "benchmarks", "kld-base"), new BenchmarkKldBaseCache(Substitute.For<IFreeSpaceProbe>()).Root);
            AssertEx.Equal(Path.Combine(root, "llama.cpp"), LlamaCppBinaryManager.DefaultLlamaCppBinariesRoot());
            AssertEx.Equal(Path.Combine(root, "stable-diffusion.cpp"), StableDiffusionCppBinaryManager.DefaultStableDiffusionBinariesRoot());
            AssertEx.Equal(Path.Combine(root, "whisper.cpp"), WhisperCppBinaryManager.DefaultWhisperBinariesRoot());
            await VerifyStoresAsync(root);
            await VerifyRecoveryAsync(root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeCacheDirectory.EnvironmentVariable, original);
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void InvalidEnvironmentOverride_DoesNotFallBack()
    {
        var original = Environment.GetEnvironmentVariable(RuntimeCacheDirectory.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(RuntimeCacheDirectory.EnvironmentVariable, "relative");
            AssertEx.Throws<InvalidOperationException>(() => RuntimeCacheDirectory.Resolve());
            AssertEx.Throws<InvalidOperationException>(() => _ = new InstalledRuntimeStore());
            AssertEx.Throws<InvalidOperationException>(() => _ = new StableDiffusionInstalledRuntimeStore());
            AssertEx.Throws<InvalidOperationException>(() => _ = new WhisperInstalledRuntimeStore());
            AssertEx.Throws<InvalidOperationException>(() => TrainingRuntimeLayout.DefaultCacheRoot());
            AssertEx.Throws<InvalidOperationException>(() => _ = new BenchmarkKldBaseCache(Substitute.For<IFreeSpaceProbe>()));
            using var http = new HttpClient();
            AssertEx.Throws<InvalidOperationException>(() =>
            {
                using var compute = new ComputePythonEnvironment(http, NullLogger<ComputePythonEnvironment>.Instance);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeCacheDirectory.EnvironmentVariable, original);
        }
    }

    private static async Task VerifyStoresAsync(string root)
    {
        using var llama = new InstalledRuntimeStore();
        using var images = new StableDiffusionInstalledRuntimeStore();
        using var whisper = new WhisperInstalledRuntimeStore();
        var llamaState = new InstalledRuntimeState("test", "test", new string('a', 64), GpuVariant.Cpu, DateTimeOffset.UtcNow);
        var imageState = new StableDiffusionInstalledRuntimeState(StableDiffusionInstalledRuntimeValidity.Invalid,
            SdGpuBackend.Cpu, StableDiffusionCppSourceBuildRequestValidation.OfficialRepository, StableDiffusionReleasePins.PinnedSourceCommitSha,
            StableDiffusionCppSourceSelection.Official, StableDiffusionCppSourceRevisionMode.EnginePinned,
            null, null, null, DateTimeOffset.UtcNow, "Test tombstone");
        var whisperState = new WhisperInstalledRuntimeState(WhisperInstalledRuntimeValidity.Invalid,
            WhisperBackend.Cpu, WhisperCppSourceBuildRequestValidation.OfficialRepository, WhisperCppReleasePins.PinnedSourceCommitSha,
            WhisperCppSourceSelection.Official, WhisperCppSourceRevisionMode.EnginePinned,
            null, null, null, DateTimeOffset.UtcNow, "Test tombstone");
        await llama.WriteAsync(llamaState, CancellationToken.None);
        await images.WriteAsync(imageState, CancellationToken.None);
        await whisper.WriteAsync(whisperState, CancellationToken.None);
        AssertEx.Equal(llamaState, await llama.ReadAsync(CancellationToken.None));
        AssertEx.Equal(imageState, await images.ReadAsync(CancellationToken.None));
        AssertEx.Equal(whisperState, await whisper.ReadAsync(CancellationToken.None));
        AssertEx.True(File.Exists(Path.Combine(root, "installed-runtime.json")));
        foreach (var runtime in new[]
                 {
                     "stable-diffusion.cpp",
                     "whisper.cpp"
                 })
        {
            AssertEx.True(File.Exists(Path.Combine(root, runtime, "installed-runtime.json")));
            AssertEx.True(File.Exists(Path.Combine(root, runtime, "desired-runtime.json")));
        }

        await llama.DeleteAsync(CancellationToken.None);
        await images.DeleteAsync(CancellationToken.None);
        await whisper.DeleteAsync(CancellationToken.None);
    }

    private static async Task VerifyRecoveryAsync(string root)
    {
        using var llamaStore = new InstalledRuntimeStore();
        using var imageStore = new StableDiffusionInstalledRuntimeStore();
        using var whisperStore = new WhisperInstalledRuntimeStore();
        using var llama = new LlamaCppSourceBuildService(Substitute.For<ILlamaCppSourceBuildPrerequisiteProbe>(),
            Substitute.For<ILlamaCppBinaryManager>(), llamaStore, new CudaManagedBuildSignal(),
            Substitute.For<ILlamaServerProcessSupervisor>(), new LlamaCppSourceBuildActivity(),
            Substitute.For<ILlamaCppSourceBuildEventPublisher>(), NullLogger<LlamaCppSourceBuildService>.Instance, TimeProvider.System);
        using var images = new StableDiffusionCppSourceBuildService(Substitute.For<IStableDiffusionCppSourceBuildPrerequisiteProbe>(),
            imageStore, new StableDiffusionManagedSourceBuildSignal(), new ImageRuntimeActivityGate(),
            Substitute.For<IStableDiffusionCppSourceBuildEventPublisher>(), NullLogger<StableDiffusionCppSourceBuildService>.Instance, TimeProvider.System);
        using var whisper = new WhisperCppSourceBuildService(Substitute.For<IWhisperCppSourceBuildPrerequisiteProbe>(),
            whisperStore, new WhisperManagedSourceBuildSignal(), new WhisperRuntimeActivityGate(),
            Substitute.For<IWhisperCppSourceBuildEventPublisher>(), NullLogger<WhisperCppSourceBuildService>.Instance, TimeProvider.System);
        var workDirectories = new[]
                              {
                                  "llama.cpp",
                                  "stable-diffusion.cpp",
                                  "whisper.cpp"
                              }
                              .Select(runtime => Path.Combine(root, runtime, "source-build", ".work")).ToArray();
        foreach (var work in workDirectories)
        {
            Directory.CreateDirectory(work);
            await File.WriteAllTextAsync(Path.Combine(work, "interrupted.txt"), "scratch");
        }

        var staging = Path.Combine(root, "llama.cpp", "source-build", ".staging");
        Directory.CreateDirectory(staging);
        await llama.RecoverAsync(CancellationToken.None);
        await images.RecoverAsync(CancellationToken.None);
        await whisper.RecoverAsync(CancellationToken.None);
        foreach (var work in workDirectories)
        {
            AssertEx.False(Directory.Exists(work));
        }

        AssertEx.False(Directory.Exists(staging));
        await images.ShutdownAsync(CancellationToken.None);
        await whisper.ShutdownAsync(CancellationToken.None);
    }
}
