namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class SourceBuildRecoveryTests
{
    [Test]
    public async Task Recover_ActiveManifestVariantMismatch_DiscardsOrphanAndVersionsSignal()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var active = Path.Combine(temp.Path, "llama.cpp", "source-build", "active");
        var state = await SeedTreeAndStateAsync(active, GpuVariant.Cpu, store, manifestVariant: GpuVariant.Vulkan);
        var signal = new CudaManagedBuildSignal(); signal.SetActive(GpuVariant.Cpu); var before = signal.Version;
        using var service = CreateService(temp.Path, store, signal);

        await service.RecoverAsync(CancellationToken.None);

        AssertEx.False(Directory.Exists(active));
        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
        AssertEx.Null(signal.ActiveVariant);
        AssertEx.True(signal.Version > before);
        _ = state;
    }

    [Test]
    public async Task Recover_BackupOnlyMatchingManifest_RestoresActive()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var sourceRoot = Path.Combine(temp.Path, "llama.cpp", "source-build");
        var backup = Path.Combine(sourceRoot, ".backup");
        await SeedTreeAndStateAsync(backup, GpuVariant.Cpu, store, manifestVariant: GpuVariant.Cpu);
        var signal = new CudaManagedBuildSignal();
        using var service = CreateService(temp.Path, store, signal);

        await service.RecoverAsync(CancellationToken.None);

        var active = Path.Combine(sourceRoot, "active");
        AssertEx.True(Directory.Exists(active));
        AssertEx.False(Directory.Exists(backup));
        AssertEx.Equal(Path.Combine(active, "build", "bin"), (await store.ReadAsync(CancellationToken.None))!.SourceBuildPath);
        AssertEx.Equal(GpuVariant.Cpu, signal.ActiveVariant);
    }

    [Test]
    public async Task Recover_ActiveAndBackup_RestoresOnlyTreeMatchingFullDescriptor()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var sourceRoot = Path.Combine(temp.Path, "llama.cpp", "source-build");
        var backup = Path.Combine(sourceRoot, ".backup");
        var active = Path.Combine(sourceRoot, "active");
        await SeedTreeAndStateAsync(backup, GpuVariant.Cpu, store, manifestVariant: GpuVariant.Cpu);
        await SeedTreeAndStateAsync(active, GpuVariant.Cpu, store, manifestVariant: GpuVariant.Vulkan);
        var signal = new CudaManagedBuildSignal();
        using var service = CreateService(temp.Path, store, signal);

        await service.RecoverAsync(CancellationToken.None);

        AssertEx.True(Directory.Exists(active));
        AssertEx.False(Directory.Exists(backup));
        AssertEx.Equal(GpuVariant.Cpu, signal.ActiveVariant);
    }

    [Test]
    public async Task Recover_PreProvenanceLegacyCuda_PreservesValidatedRecord()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var bin = Path.Combine(temp.Path, "llama.cpp", "source-cuda", LlamaCppReleasePins.PinnedTag, "build", "bin");
        var server = WriteServer(bin, GpuVariant.Cuda);
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(server)));
        await store.WriteAsync(new InstalledRuntimeState(LlamaCppReleasePins.PinnedTag, "(source-build:cuda)", sha, GpuVariant.Cuda,
            DateTimeOffset.UtcNow, bin), CancellationToken.None);
        var signal = new CudaManagedBuildSignal();
        using var service = CreateService(temp.Path, store, signal);

        await service.RecoverAsync(CancellationToken.None);

        AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));
        AssertEx.Equal(GpuVariant.Cuda, signal.ActiveVariant);
        AssertEx.True(File.Exists(server));
    }

    [Test]
    public async Task Start_WhenRecoveryStoreFails_BlocksAndPreservesBackup()
    {
        using var temp = new TempDirectory();
        var backup = Path.Combine(temp.Path, "llama.cpp", "source-build", ".backup");
        Directory.CreateDirectory(backup);
        await File.WriteAllTextAsync(Path.Combine(backup, "sentinel"), "keep");
        var store = new ThrowingStore();
        var signal = new CudaManagedBuildSignal();
        using var service = CreateService(temp.Path, store, signal);

        await AssertEx.ThrowsAsync<IOException>(() => service.StartAsync(new LlamaCppSourceBuildRequest(
            LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Official), CancellationToken.None));

        AssertEx.True(File.Exists(Path.Combine(backup, "sentinel")));
    }

    private static async Task<InstalledRuntimeState> SeedTreeAndStateAsync(string tree, GpuVariant variant, IInstalledRuntimeStore store, GpuVariant manifestVariant)
    {
        var bin = Path.Combine(tree, "build", "bin");
        var server = WriteServer(bin, variant);
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(server)));
        var activeBin = Path.Combine(Path.GetDirectoryName(tree)!, "active", "build", "bin");
        var state = new InstalledRuntimeState(LlamaCppReleasePins.PinnedTag, "source", sha, variant, DateTimeOffset.UtcNow, activeBin,
            LlamaCppSourceBuildRequestValidation.OfficialRepository, LlamaCppReleasePins.PinnedSourceCommitSha,
            LlamaCppSourceRevisionMode.EnginePinned);
        await store.WriteAsync(state, CancellationToken.None);
        var manifest = new
        {
            Tag = LlamaCppReleasePins.PinnedTag,
            Variant = manifestVariant,
            Source = LlamaCppSourceSelection.Official,
            Repository = LlamaCppSourceBuildRequestValidation.OfficialRepository,
            RevisionMode = LlamaCppSourceRevisionMode.EnginePinned,
            RequestedCommit = (string?)null,
            ResolvedCommit = LlamaCppReleasePins.PinnedSourceCommitSha,
            BinarySha256 = sha
        };
        await File.WriteAllTextAsync(Path.Combine(tree, ".source-build-manifest.json"), JsonSerializer.Serialize(manifest));
        return state;
    }

    private static string WriteServer(string bin, GpuVariant variant)
    {
        Directory.CreateDirectory(bin);
        var device = variant == GpuVariant.Cuda ? "CUDA0:" : "Vulkan0:";
        var path = Path.Combine(bin, "llama-server");
        File.WriteAllText(path, $"#!/bin/sh\ncase \"$1\" in --version) exit 0;; --list-devices) echo '{device} test'; exit 0;; esac\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    private static LlamaCppSourceBuildService CreateService(string root, IInstalledRuntimeStore store, IActiveSourceBuildSignal signal) =>
        new(new ReadyProbe(), new NoopManager(), store, signal, new LeaseSupervisor(), new NullLlamaCppSourceBuildEventPublisher(),
            NullLogger<LlamaCppSourceBuildService>.Instance, root);

    private sealed class ReadyProbe : ILlamaCppSourceBuildPrerequisiteProbe { public Task<LlamaCppSourceBuildPrerequisiteReport> ProbeAsync(LlamaCppSourceBackend backend, CancellationToken ct) => Task.FromResult(new LlamaCppSourceBuildPrerequisiteReport(true, [])); }
    private sealed class NoopManager : ILlamaCppBinaryManager
    {
        public Task<LlamaBinary> EnsureBinaryAsync(GpuVariant variant, CancellationToken ct) => throw new NotSupportedException();
        public Task<LlamaBinary> InstallTagAsync(string tag, string assetName, string digestSha256, long expectedSize, GpuVariant variant, CancellationToken ct) => throw new NotSupportedException();
        public Task<InstalledRuntimeState> AdoptCudaSourceBuildAsync(string buildBinDir, string tag, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveCudaSourceBuildAsync(CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class LeaseSupervisor : ILlamaServerProcessSupervisor
    {
        public Task<LlamaServerEndpoint> EnsureRunningAsync(string modelName, ModelRole role, CancellationToken ct) => throw new NotSupportedException(); public Task EvictAsync(string modelName, ModelRole role, CancellationToken ct) => throw new NotSupportedException(); public Task<LlamaServerEjectOutcome> EjectAsync(string modelName, ModelRole role, bool force, CancellationToken ct) => throw new NotSupportedException(); public LlamaServerLeaseAcquisition TryAcquireInferenceLease(string modelName, ModelRole role) => throw new NotSupportedException(); public Task<T> RunExclusiveProfilingAsync<T>(string modelName, ModelRole role, ResolvedLaunchArguments launchArgs, bool enableMetrics, Func<LlamaServerProfilingContext, CancellationToken, Task<T>> body, CancellationToken ct) => throw new NotSupportedException(); public Task<IReadOnlyList<LlamaServerProcessHealth>> CheckHealthAsync(CancellationToken ct) => throw new NotSupportedException(); public int CountRunningProcesses() => 0; public LlamaServerRuntimeInfo? GetRuntimeInfo(string modelName, ModelRole role) => null;
    }
    private sealed class ThrowingStore : IInstalledRuntimeStore { public Task<InstalledRuntimeState?> ReadAsync(CancellationToken ct) => throw new IOException("read failed"); public Task WriteAsync(InstalledRuntimeState state, CancellationToken ct) => throw new NotSupportedException(); public Task DeleteAsync(CancellationToken ct) => throw new NotSupportedException(); }
    private sealed class TempDirectory : IDisposable { public TempDirectory() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); } public string Path { get; } public void Dispose() { try { Directory.Delete(Path, true); } catch (Exception) { /* Best effort. */ } } }
}
