namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[NotInParallel]
public sealed class LlamaCppSourceBuildServiceTests
{
    [Test]
    public async Task Cancel_CustomCpu_IsRejectedByLegacyPredicateAndGenericCancelRetainsActiveRuntime()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var (previousState, previousServer) = await SeedActiveRuntimeAsync(temp.Path, store);
        var stubs = Path.Combine(temp.Path, "stubs");
        Directory.CreateDirectory(stubs);
        WriteScript(Path.Combine(stubs, "git"), "#!/bin/sh\nif [ \"$1\" = \"clone\" ]; then sleep 5; for last; do :; done; mkdir -p \"$last\"; exit 0; fi\n");
        using var path = new PathScope(stubs);
        var signal = new CudaManagedBuildSignal();
        signal.SetActive(GpuVariant.Cpu);
        using var service = new LlamaCppSourceBuildService(new AlwaysReadyProbe(), new CapturingBinaryManager(store, signal), store, signal,
            new LeaseOnlySupervisor(), new NullLlamaCppSourceBuildEventPublisher(), NullLogger<LlamaCppSourceBuildService>.Instance, temp.Path);

        var start = await service.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Custom,
            "https://github.com/example/fork", AcknowledgeCustomSourceRisk: true), CancellationToken.None);
        AssertEx.Equal(LlamaCppSourceBuildStartOutcome.Started, start);
        AssertEx.False(service.CancelLegacyPinnedCuda());
        AssertEx.True(service.Cancel());
        await AssertEx.EventuallyAsync(() => service.GetStatus().Terminal, TimeSpan.FromSeconds(10));

        AssertEx.Equal(LlamaCppSourceBuildPhase.Cancelled, service.GetStatus().Phase);
        AssertEx.True(File.Exists(previousServer));
        AssertEx.Equal(previousState, await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    public async Task Start_WhenResolvedCommitMismatches_StopsBeforeCmakeAndRecordsNothing()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temp = new TempDirectory();
        var stubs = Path.Combine(temp.Path, "stubs");
        Directory.CreateDirectory(stubs);
        var cmakeMarker = Path.Combine(temp.Path, "cmake-ran");
        WriteScript(Path.Combine(stubs, "git"), "#!/bin/sh\nif [ \"$1\" = \"clone\" ]; then for last; do :; done; mkdir -p \"$last\"; exit 0; fi\nif [ \"$1\" = \"-C\" ]; then echo '0000000000000000000000000000000000000000'; exit 0; fi\n");
        WriteScript(Path.Combine(stubs, "cmake"), $"#!/bin/sh\ntouch '{cmakeMarker}'\n");
        using var path = new PathScope(stubs);
        using var store = new InstalledRuntimeStore(temp.Path);
        var signal = new CudaManagedBuildSignal();
        using var service = new LlamaCppSourceBuildService(new AlwaysReadyProbe(), new CapturingBinaryManager(store, signal), store, signal,
            new LeaseOnlySupervisor(), new NullLlamaCppSourceBuildEventPublisher(), NullLogger<LlamaCppSourceBuildService>.Instance, temp.Path);

        await service.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Official), CancellationToken.None);
        await AssertEx.EventuallyAsync(() => service.GetStatus().Terminal, TimeSpan.FromSeconds(10));

        AssertEx.Equal(LlamaCppSourceBuildPhase.Failed, service.GetStatus().Phase);
        AssertEx.False(File.Exists(cmakeMarker));
        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    public async Task Start_WhenUnexpectedAdoptionFailure_UsesGenericSourceBuildMessage()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temp = new TempDirectory();
        var stubs = Path.Combine(temp.Path, "stubs");
        Directory.CreateDirectory(stubs);
        WriteScript(Path.Combine(stubs, "git"), $"#!/bin/sh\nif [ \"$1\" = \"clone\" ]; then for last; do :; done; mkdir -p \"$last\"; exit 0; fi\nif [ \"$1\" = \"-C\" ]; then echo '{LlamaCppReleasePins.PinnedSourceCommitSha}'; exit 0; fi\n");
        WriteScript(Path.Combine(stubs, "cmake"), "#!/bin/sh\nif [ \"$1\" = \"-B\" ]; then mkdir -p \"$2\"; exit 0; fi\nif [ \"$1\" = \"--build\" ]; then mkdir -p \"$2/bin\"; printf '#!/bin/sh\\nexit 0\\n' > \"$2/bin/llama-server\"; chmod 755 \"$2/bin/llama-server\"; exit 0; fi\n");
        using var path = new PathScope(stubs);
        using var store = new InstalledRuntimeStore(temp.Path);
        var signal = new CudaManagedBuildSignal();
        using var service = new LlamaCppSourceBuildService(new AlwaysReadyProbe(),
            new CapturingBinaryManager(store, signal, failAdoption: true),
            store,
            signal,
            new LeaseOnlySupervisor(),
            new NullLlamaCppSourceBuildEventPublisher(),
            NullLogger<LlamaCppSourceBuildService>.Instance,
            temp.Path);

        await service.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Official), CancellationToken.None);
        await AssertEx.EventuallyAsync(() => service.GetStatus().Terminal, TimeSpan.FromSeconds(10));

        AssertEx.Equal(LlamaCppSourceBuildPhase.Failed, service.GetStatus().Phase);
        AssertEx.Equal("The source build failed unexpectedly.", service.GetStatus().SanitizedError);
    }

    [Test]
    public async Task Start_OfficialCpu_UsesPinnedCommitScrubbedGitAndCpuMatrix()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temp = new TempDirectory();
        var stubs = Path.Combine(temp.Path, "stubs");
        Directory.CreateDirectory(stubs);
        var envDump = Path.Combine(temp.Path, "env.txt");
        var cmakeArgs = Path.Combine(temp.Path, "cmake.txt");
        WriteScript(Path.Combine(stubs, "git"), $"#!/bin/sh\nif [ \"$1\" = \"clone\" ]; then env > '{envDump}'; for last; do :; done; mkdir -p \"$last\"; exit 0; fi\nif [ \"$1\" = \"-C\" ]; then echo '{LlamaCppReleasePins.PinnedSourceCommitSha}'; exit 0; fi\nexit 0\n");
        WriteScript(Path.Combine(stubs, "cmake"), $"#!/bin/sh\necho \"$@\" >> '{cmakeArgs}'\nif [ \"$1\" = \"-B\" ]; then mkdir -p \"$2\"; exit 0; fi\nif [ \"$1\" = \"--build\" ]; then mkdir -p \"$2/bin\"; printf '#!/bin/sh\\nexit 0\\n' > \"$2/bin/llama-server\"; chmod 755 \"$2/bin/llama-server\"; exit 0; fi\nexit 0\n");
        using var path = new PathScope(stubs);
        Environment.SetEnvironmentVariable("XE_NODE_SQLITE_KEY", "must-not-leak");
        try
        {
            using var store = new InstalledRuntimeStore(temp.Path);
            var signal = new CudaManagedBuildSignal();
            var manager = new CapturingBinaryManager(store, signal);
            using var service = new LlamaCppSourceBuildService(new AlwaysReadyProbe(),
                manager,
                store,
                signal,
                new LeaseOnlySupervisor(),
                new NullLlamaCppSourceBuildEventPublisher(),
                NullLogger<LlamaCppSourceBuildService>.Instance,
                temp.Path);

            var outcome = await service.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Official), CancellationToken.None);
            AssertEx.Equal(LlamaCppSourceBuildStartOutcome.Started, outcome);
            await AssertEx.EventuallyAsync(() => service.GetStatus().Terminal, TimeSpan.FromSeconds(10));

            AssertEx.Equal(LlamaCppSourceBuildPhase.Completed, service.GetStatus().Phase);
            AssertEx.Equal(GpuVariant.Cpu, manager.AdoptedVariant);
            var args = await File.ReadAllTextAsync(cmakeArgs);
            AssertEx.True(args.Contains("-DGGML_CUDA=OFF", StringComparison.Ordinal));
            AssertEx.True(args.Contains("-DGGML_VULKAN=OFF", StringComparison.Ordinal));
            AssertEx.False(args.Contains("CMAKE_CUDA_ARCHITECTURES", StringComparison.Ordinal));
            var environment = await File.ReadAllTextAsync(envDump);
            AssertEx.False(environment.Contains("must-not-leak", StringComparison.Ordinal));
            AssertEx.True(environment.Contains("GIT_CONFIG_NOSYSTEM=1", StringComparison.Ordinal));
            AssertEx.True(environment.Contains($"HOME={Path.Combine(temp.Path, "llama.cpp", "source-build", ".work", ".home")}", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XE_NODE_SQLITE_KEY", null);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void WriteScript(string path, string content)
    {
        File.WriteAllText(path, content);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed class AlwaysReadyProbe : ILlamaCppSourceBuildPrerequisiteProbe
    {
        public Task<LlamaCppSourceBuildPrerequisiteReport> ProbeAsync(LlamaCppSourceBackend backend, CancellationToken ct) =>
            Task.FromResult(new LlamaCppSourceBuildPrerequisiteReport(true, []));
    }

    private sealed class CapturingBinaryManager(IInstalledRuntimeStore store, IActiveSourceBuildSignal signal, bool failAdoption = false) : ILlamaCppBinaryManager
    {
        public GpuVariant? AdoptedVariant { get; private set; }
        public Task<LlamaBinary> EnsureBinaryAsync(GpuVariant variant, CancellationToken ct) => throw new NotSupportedException();
        public Task<LlamaBinary> InstallTagAsync(string tag, string assetName, string digestSha256, long expectedSize, GpuVariant variant, CancellationToken ct) => throw new NotSupportedException();
        public Task<InstalledRuntimeState> AdoptCudaSourceBuildAsync(string buildBinDir, string tag, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveCudaSourceBuildAsync(CancellationToken ct) => Task.CompletedTask;

        public async Task<InstalledRuntimeState> AdoptSourceBuildAsync(string buildBinDir, string tag, GpuVariant variant, string sourceRepository,
            string sourceCommit, LlamaCppSourceRevisionMode revisionMode, string? requestedCommit, CancellationToken ct)
        {
            if (failAdoption)
            {
                throw new InvalidOperationException("unexpected adoption failure");
            }

            AdoptedVariant = variant;
            var state = new InstalledRuntimeState(tag, "source", new string('a', 64), variant, DateTimeOffset.UtcNow, buildBinDir,
                sourceRepository, sourceCommit, revisionMode, requestedCommit);
            await store.WriteAsync(state, ct);
            signal.SetActive(variant);
            return state;
        }
    }

    private sealed class LeaseOnlySupervisor : ILlamaServerProcessSupervisor
    {
        public Task<ILlamaServerRuntimeMutationLease?> TryAcquireRuntimeMutationLeaseAsync(CancellationToken ct) => Task.FromResult<ILlamaServerRuntimeMutationLease?>(new Lease());
        public Task<LlamaServerEndpoint> EnsureRunningAsync(string modelName, ModelRole role, CancellationToken ct) => throw new NotSupportedException();
        public Task EvictAsync(string modelName, ModelRole role, CancellationToken ct) => throw new NotSupportedException();
        public Task<LlamaServerEjectOutcome> EjectAsync(string modelName, ModelRole role, bool force, CancellationToken ct) => throw new NotSupportedException();
        public LlamaServerLeaseAcquisition TryAcquireInferenceLease(string modelName, ModelRole role) => throw new NotSupportedException();
        public Task<T> RunExclusiveProfilingAsync<T>(string modelName, ModelRole role, ResolvedLaunchArguments launchArgs, bool enableMetrics, Func<LlamaServerProfilingContext, CancellationToken, Task<T>> body, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<LlamaServerProcessHealth>> CheckHealthAsync(CancellationToken ct) => throw new NotSupportedException();
        public int CountRunningProcesses() => 0;
        public LlamaServerRuntimeInfo? GetRuntimeInfo(string modelName, ModelRole role) => null;
        private sealed class Lease : ILlamaServerRuntimeMutationLease { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    private sealed class PathScope : IDisposable
    {
        private readonly string _original = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        public PathScope(string path) => Environment.SetEnvironmentVariable("PATH", path + Path.PathSeparator + _original);
        public void Dispose() => Environment.SetEnvironmentVariable("PATH", _original);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-source-build-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
        public string Path { get; }
        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception)
            {
                // Best-effort test cleanup.
            }
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task<(InstalledRuntimeState State, string Server)> SeedActiveRuntimeAsync(string root, IInstalledRuntimeStore store)
    {
        var tree = Path.Combine(root, "llama.cpp", "source-build", "active");
        var bin = Path.Combine(tree, "build", "bin");
        Directory.CreateDirectory(bin);
        var server = Path.Combine(bin, "llama-server");
        await File.WriteAllTextAsync(server, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(server, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(server)));
        var state = new InstalledRuntimeState(LlamaCppReleasePins.PinnedTag, "source", sha, GpuVariant.Cpu, DateTimeOffset.UtcNow, bin,
            LlamaCppSourceBuildRequestValidation.OfficialRepository, LlamaCppReleasePins.PinnedSourceCommitSha,
            LlamaCppSourceRevisionMode.EnginePinned);
        await store.WriteAsync(state, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(tree, ".source-build-manifest.json"), JsonSerializer.Serialize(new
        {
            Tag = state.Tag,
            Variant = state.Variant,
            Source = LlamaCppSourceSelection.Official,
            Repository = state.SourceRepository,
            RevisionMode = state.SourceRevisionMode,
            RequestedCommit = state.SourceRequestedCommit,
            ResolvedCommit = state.SourceCommit,
            BinarySha256 = state.Sha256
        }));
        return (state, server);
    }
}
