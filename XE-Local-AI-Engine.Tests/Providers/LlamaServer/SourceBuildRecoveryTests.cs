namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

public sealed class SourceBuildRecoveryTests
{
    [Test]
    [RunOn(OS.Linux)]
    public async Task Recover_ActiveManifestVariantMismatch_DiscardsOrphanAndVersionsSignal()
    {
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var active = Path.Combine(temp.Path, "llama.cpp", "source-build", "active");
        var state = await SeedTreeAndStateAsync(active, GpuVariant.Cpu, store, manifestVariant: GpuVariant.Vulkan);
        var signal = new CudaManagedBuildSignal();
        signal.SetActive(GpuVariant.Cpu);
        var before = signal.Version;
        using var service = CreateService(temp.Path, store, signal);

        await service.RecoverAsync(CancellationToken.None);

        AssertEx.False(Directory.Exists(active));
        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
        AssertEx.Null(signal.ActiveVariant);
        AssertEx.True(signal.Version > before);
        _ = state;
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Recover_BackupOnlyMatchingManifest_RestoresActive()
    {
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
    [RunOn(OS.Linux)]
    public async Task Recover_CustomSelectionUsingOfficialRepository_PreservesExplicitSelection()
    {
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var active = Path.Combine(temp.Path, "llama.cpp", "source-build", "active");
        await SeedTreeAndStateAsync(active,
            GpuVariant.Cpu,
            store,
            GpuVariant.Cpu,
            LlamaCppSourceSelection.Custom,
            LlamaCppSourceRevisionMode.DefaultBranch);
        var signal = new CudaManagedBuildSignal();
        using var service = CreateService(temp.Path, store, signal);

        await service.RecoverAsync(CancellationToken.None);

        AssertEx.True(Directory.Exists(active));
        AssertEx.Equal(LlamaCppSourceSelection.Custom, (await store.ReadAsync(CancellationToken.None))!.SourceSelection);
        AssertEx.Equal(GpuVariant.Cpu, signal.ActiveVariant);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Recover_InvalidOfficialProvenance_DiscardsTreeAndRecord()
    {
        var cases = new[]
        {
            (Repository: LlamaCppSourceBuildRequestValidation.OfficialRepository,
                Revision: LlamaCppSourceRevisionMode.DefaultBranch,
                Requested: (string?)null,
                Resolved: LlamaCppReleasePins.PinnedSourceCommitSha),
            (Repository: "https://github.com/example/fork",
                Revision: LlamaCppSourceRevisionMode.EnginePinned,
                Requested: (string?)null,
                Resolved: LlamaCppReleasePins.PinnedSourceCommitSha),
            (Repository: LlamaCppSourceBuildRequestValidation.OfficialRepository,
                Revision: LlamaCppSourceRevisionMode.EnginePinned,
                Requested: LlamaCppReleasePins.PinnedSourceCommitSha,
                Resolved: LlamaCppReleasePins.PinnedSourceCommitSha),
            (Repository: LlamaCppSourceBuildRequestValidation.OfficialRepository,
                Revision: LlamaCppSourceRevisionMode.EnginePinned,
                Requested: (string?)null,
                Resolved: new string('a', 40))
        };

        foreach (var (repository, revision, requested, resolved) in cases)
        {
            using var temp = new TempDirectory();
            using var store = new InstalledRuntimeStore(temp.Path);
            var active = Path.Combine(temp.Path, "llama.cpp", "source-build", "active");
            await SeedTreeAndStateAsync(active,
                GpuVariant.Cpu,
                store,
                GpuVariant.Cpu,
                LlamaCppSourceSelection.Official,
                revision,
                sourceRepository: repository,
                requestedCommit: requested,
                resolvedCommit: resolved);
            var signal = new CudaManagedBuildSignal();
            using var service = CreateService(temp.Path, store, signal);

            await service.RecoverAsync(CancellationToken.None);

            AssertEx.False(Directory.Exists(active));
            AssertEx.Null(await store.ReadAsync(CancellationToken.None));
            AssertEx.Null(signal.ActiveVariant);
        }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Recover_ActiveTreeValidationRunsFromBinaryDirectory()
    {
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var active = Path.Combine(temp.Path, "llama.cpp", "source-build", "active");
        await SeedTreeAndStateAsync(active, GpuVariant.Cpu, store, GpuVariant.Cpu, requireBinaryWorkingDirectory: true);
        var signal = new CudaManagedBuildSignal();
        using var service = CreateService(temp.Path, store, signal);

        await service.RecoverAsync(CancellationToken.None);

        AssertEx.True(Directory.Exists(active));
        AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));
        AssertEx.Equal(GpuVariant.Cpu, signal.ActiveVariant);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Recover_ActiveAndBackup_RestoresOnlyTreeMatchingFullDescriptor()
    {
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
    [RunOn(OS.Linux)]
    public async Task Recover_PreProvenanceLegacyCuda_PreservesValidatedRecord()
    {
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

    /// <summary>
    ///     A record naming a DIFFERENT directory says nothing about the active tree, so reconciliation must leave the
    ///     tree (and the record) alone. Deleting it treated "the record does not describe this tree" as "this tree is
    ///     garbage".
    /// </summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task Recover_ActiveTreeWithUnboundRecordedPath_LeavesTreeAndRecordIntact()
    {
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var active = Path.Combine(temp.Path, "llama.cpp", "source-build", "active");
        var state = await SeedTreeAndStateAsync(active, GpuVariant.Cpu, store, manifestVariant: GpuVariant.Cpu);
        var unbound = Path.Combine(temp.Path, "unbound", "build", "bin");
        await store.WriteAsync(state with
        {
            SourceBuildPath = unbound
        }, CancellationToken.None);
        var logger = new RecordingLogger<LlamaCppSourceBuildService>();
        var signal = new CudaManagedBuildSignal();
        using var service = CreateService(temp.Path, store, signal, logger);

        await service.RecoverAsync(CancellationToken.None);

        AssertEx.True(Directory.Exists(active));
        AssertEx.Equal(unbound, (await store.ReadAsync(CancellationToken.None))!.SourceBuildPath);
        AssertEx.True(logger.HasEntry(LogLevel.Warning, unbound));
    }

    /// <summary>
    ///     The 2026-09-02 data loss: <c>installed-runtime.json</c> is user-level and shared by every checkout, so a node
    ///     on a fresh database recorded an auto-acquired Vulkan prebuilt over the operator's managed CUDA record. The
    ///     next start read that prebuilt record — which carries no source-build path at all — as authority to delete the
    ///     source build. A record that names no source build is not authority over one.
    /// </summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task Recover_ActiveTreeWithPrebuiltRecord_LeavesTreeAndRecordIntact()
    {
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var sourceRoot = Path.Combine(temp.Path, "llama.cpp", "source-build");
        var active = Path.Combine(sourceRoot, "active");
        var backup = Path.Combine(sourceRoot, ".backup");
        await SeedTreeAndStateAsync(active, GpuVariant.Cuda, store, manifestVariant: GpuVariant.Cuda);
        await SeedTreeAndStateAsync(backup, GpuVariant.Cuda, store, manifestVariant: GpuVariant.Cuda);
        var prebuilt = new InstalledRuntimeState(LlamaCppReleasePins.PinnedTag,
            "llama-" + LlamaCppReleasePins.PinnedTag + "-bin-ubuntu-vulkan-x64.tar.gz",
            new string('a', 64),
            GpuVariant.Vulkan,
            DateTimeOffset.UtcNow);
        await store.WriteAsync(prebuilt, CancellationToken.None);
        var logger = new RecordingLogger<LlamaCppSourceBuildService>();
        var signal = new CudaManagedBuildSignal();
        using var service = CreateService(temp.Path, store, signal, logger);

        await service.RecoverAsync(CancellationToken.None);

        AssertEx.True(Directory.Exists(active), "A prebuilt record must never delete a managed source build.");
        AssertEx.True(Directory.Exists(backup), "A prebuilt record must never delete the source-build backup either.");
        AssertEx.Null((await store.ReadAsync(CancellationToken.None))!.SourceBuildPath);
        AssertEx.True(logger.HasEntry(LogLevel.Warning, active));
    }

    /// <summary>
    ///     Absence of the active tree self-heals only a record that NAMES it. A record pointing at a relocated data
    ///     directory describes a tree this reconcile never looked at, so erasing it loses a working source build.
    /// </summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task Recover_NoTreesWithRecordNamingAnotherPath_KeepsRecord()
    {
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var elsewhere = Path.Combine(temp.Path, "relocated", "source-build", "active", "build", "bin");
        var server = WriteServer(elsewhere, GpuVariant.Cuda);
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(server)));
        await store.WriteAsync(new InstalledRuntimeState(LlamaCppReleasePins.PinnedTag, "source", sha, GpuVariant.Cuda,
            DateTimeOffset.UtcNow, elsewhere, LlamaCppSourceBuildRequestValidation.OfficialRepository,
            LlamaCppReleasePins.PinnedSourceCommitSha, LlamaCppSourceRevisionMode.EnginePinned), CancellationToken.None);
        var signal = new CudaManagedBuildSignal();
        using var service = CreateService(temp.Path, store, signal);

        await service.RecoverAsync(CancellationToken.None);

        AssertEx.Equal(elsewhere, (await store.ReadAsync(CancellationToken.None))!.SourceBuildPath);
        AssertEx.True(File.Exists(server));
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Recover_BackupOnlyWithPrebuiltRecord_LeavesBackupInPlace()
    {
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var backup = Path.Combine(temp.Path, "llama.cpp", "source-build", ".backup");
        await SeedTreeAndStateAsync(backup, GpuVariant.Cuda, store, manifestVariant: GpuVariant.Cuda);
        await store.WriteAsync(new InstalledRuntimeState(LlamaCppReleasePins.PinnedTag,
            "llama-" + LlamaCppReleasePins.PinnedTag + "-bin-ubuntu-vulkan-x64.tar.gz",
            new string('a', 64),
            GpuVariant.Vulkan,
            DateTimeOffset.UtcNow), CancellationToken.None);
        var logger = new RecordingLogger<LlamaCppSourceBuildService>();
        var signal = new CudaManagedBuildSignal();
        using var service = CreateService(temp.Path, store, signal, logger);

        await service.RecoverAsync(CancellationToken.None);

        AssertEx.True(Directory.Exists(backup), "A prebuilt record must not delete the parked previous runtime either.");
        AssertEx.True(logger.HasEntry(LogLevel.Warning, "(none)"));
    }

    [Test]
    public async Task Startup_WhenRecoveryFails_PropagatesAndBlocksStartup()
    {
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var service = new CudaBuildStartupService(new FailingRecoveryBuildService(),
            store,
            new CudaManagedBuildSignal(),
            NullLogger<CudaBuildStartupService>.Instance);

        await AssertEx.ThrowsAsync<IOException>(() => service.StartAsync(CancellationToken.None));
    }

    /// <summary>
    ///     A shutdown that overruns the host's budget must still exit cleanly.
    ///     <para>
    ///         <see cref="LlamaCppSourceBuildService.ShutdownAsync" /> awaits the start gate, the in-flight build and
    ///         the publisher flush on the token it is handed. The host hands it the shutdown token, which is cancelled
    ///         once <c>HostOptions.ShutdownTimeout</c> expires — so every one of those awaits throws. Because
    ///         <c>Host.StopAsync</c> aggregates and rethrows whatever a hosted service's <c>StopAsync</c> throws, an
    ///         escaping cancellation turned an over-budget shutdown into an unhandled exception and a non-zero exit,
    ///         which is what a desktop user sees when they close the app after using a model. The token means "stop
    ///         being graceful", not "throw", so the hosted-service boundary absorbs it.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Stop_WithExpiredShutdownBudget_DoesNotThrow()
    {
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        using var buildService = CreateService(temp.Path, store, new CudaManagedBuildSignal());
        var startup = new CudaBuildStartupService(buildService,
            store,
            new CudaManagedBuildSignal(),
            NullLogger<CudaBuildStartupService>.Instance);
        using var expired = new CancellationTokenSource();
        await expired.CancelAsync();

        await startup.StopAsync(expired.Token);
    }

    [Test]
    public async Task Start_WhenRecoveryStoreFails_BlocksAndPreservesBackup()
    {
        if (OperatingSystem.IsWindows())
        {
            // StartAsync refuses with "In-app source builds are available on Linux only." before it ever reaches the
            // recovery store, so there is no failing-store path to exercise here.
            Skip.Test("In-app source builds run on Linux only, so the recovery path is unreachable.");
        }

        using var temp = new TempDirectory();
        var backup = Path.Combine(temp.Path, "llama.cpp", "source-build", ".backup");
        Directory.CreateDirectory(backup);
        await File.WriteAllTextAsync(Path.Combine(backup, "sentinel"), "keep");
        var store = new ThrowingStore();
        var signal = new CudaManagedBuildSignal();
        using var service = CreateService(temp.Path, store, signal);

        await AssertEx.ThrowsAsync<IOException>(() => service.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Official), CancellationToken.None));

        AssertEx.True(File.Exists(Path.Combine(backup, "sentinel")));
    }

    private static async Task<InstalledRuntimeState> SeedTreeAndStateAsync(string tree,
        GpuVariant variant,
        IInstalledRuntimeStore store,
        GpuVariant manifestVariant,
        LlamaCppSourceSelection sourceSelection = LlamaCppSourceSelection.Official,
        LlamaCppSourceRevisionMode revisionMode = LlamaCppSourceRevisionMode.EnginePinned,
        bool requireBinaryWorkingDirectory = false,
        string? sourceRepository = null,
        string? requestedCommit = null,
        string? resolvedCommit = null)
    {
        var bin = Path.Combine(tree, "build", "bin");
        var server = WriteServer(bin, variant, requireBinaryWorkingDirectory);
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(server)));
        var activeBin = Path.Combine(Path.GetDirectoryName(tree)!, "active", "build", "bin");
        var state = new InstalledRuntimeState(LlamaCppReleasePins.PinnedTag, "source", sha, variant, DateTimeOffset.UtcNow, activeBin,
            sourceRepository ?? LlamaCppSourceBuildRequestValidation.OfficialRepository,
            resolvedCommit ?? LlamaCppReleasePins.PinnedSourceCommitSha,
            revisionMode,
            requestedCommit,
            SourceSelection: sourceSelection);
        await store.WriteAsync(state, CancellationToken.None);
        var manifest = new
        {
            Tag = LlamaCppReleasePins.PinnedTag,
            Variant = manifestVariant,
            Source = sourceSelection,
            Repository = sourceRepository ?? LlamaCppSourceBuildRequestValidation.OfficialRepository,
            RevisionMode = revisionMode,
            RequestedCommit = requestedCommit,
            ResolvedCommit = resolvedCommit ?? LlamaCppReleasePins.PinnedSourceCommitSha,
            BinarySha256 = sha
        };
        await File.WriteAllTextAsync(Path.Combine(tree, ".source-build-manifest.json"), JsonSerializer.Serialize(manifest));
        return state;
    }

    private static string WriteServer(string bin, GpuVariant variant, bool requireBinaryWorkingDirectory = false)
    {
        Directory.CreateDirectory(bin);
        var device = variant == GpuVariant.Cuda ? "CUDA0:" : "Vulkan0:";
        var path = Path.Combine(bin, "llama-server");
        var workingDirectoryCheck = requireBinaryWorkingDirectory
            ? "[ -f \"$PWD/runtime.sentinel\" ] || exit 42; "
            : string.Empty;
        if (requireBinaryWorkingDirectory)
        {
            File.WriteAllText(Path.Combine(bin, "runtime.sentinel"), "present");
        }

        File.WriteAllText(path, $"#!/bin/sh\n{workingDirectoryCheck}case \"$1\" in --version) exit 0;; --list-devices) echo '{device} test'; exit 0;; esac\n");
        var fitHelperPath = Path.Combine(bin, "llama-fit-params");
        File.WriteAllText(fitHelperPath, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(fitHelperPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static LlamaCppSourceBuildService CreateService(string root,
        IInstalledRuntimeStore store,
        IActiveSourceBuildSignal signal,
        ILogger<LlamaCppSourceBuildService>? logger = null) =>
        new(new ReadyProbe(), new NoopManager(), store, signal, new LeaseSupervisor(), new LlamaCppSourceBuildActivity(),
            new NullLlamaCppSourceBuildEventPublisher(), logger ?? NullLogger<LlamaCppSourceBuildService>.Instance, root);

    private sealed class ReadyProbe : ILlamaCppSourceBuildPrerequisiteProbe
    {
        public Task<LlamaCppSourceBuildPrerequisiteReport> ProbeAsync(LlamaCppSourceBackend backend, CancellationToken ct) =>
            Task.FromResult(new LlamaCppSourceBuildPrerequisiteReport(true, []));
    }

    private sealed class NoopManager : ILlamaCppBinaryManager
    {
        public Task<LlamaBinary> EnsureBinaryAsync(GpuVariant variant, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<LlamaBinary> InstallTagAsync(string tag, string assetName, string digestSha256, long expectedSize, GpuVariant variant, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<InstalledRuntimeState> AdoptCudaSourceBuildAsync(string buildBinDir, string tag, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RemoveCudaSourceBuildAsync(CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class LeaseSupervisor : ILlamaServerProcessSupervisor
    {
        public Task<LlamaServerEndpoint> EnsureRunningAsync(string modelName, ModelRole role, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task EvictAsync(string modelName, ModelRole role, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<LlamaServerEjectOutcome> EjectAsync(string modelName, ModelRole role, bool force, CancellationToken ct) =>
            throw new NotSupportedException();

        public LlamaServerLeaseAcquisition TryAcquireInferenceLease(string modelName, ModelRole role) =>
            throw new NotSupportedException();

        public Task<T> RunExclusiveProfilingAsync<T>(string modelName, ModelRole role, ResolvedLaunchArguments launchArgs, bool enableMetrics,
            Func<LlamaServerProfilingContext, CancellationToken, Task<T>> body, CancellationToken ct,
            Func<CancellationToken, Task<LlamaServerProfilingVramSnapshot>>? captureVramBeforeSpawn = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LlamaServerProcessHealth>> CheckHealthAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public int CountRunningProcesses() =>
            0;

        public LlamaServerRuntimeInfo? GetRuntimeInfo(string modelName, ModelRole role) =>
            null;
    }

    private sealed class ThrowingStore : IInstalledRuntimeStore
    {
        public Task<IDisposable> AcquireAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<InstalledRuntimeState?> ReadAsync(CancellationToken ct) =>
            throw new IOException("read failed");

        public Task WriteAsync(InstalledRuntimeState state, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteAsync(CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FailingRecoveryBuildService : ILlamaCppSourceBuildService
    {
        public Task<LlamaCppSourceBuildStartResult> StartAsync(LlamaCppSourceBuildRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public LlamaCppSourceBuildStatus GetStatus() =>
            throw new NotSupportedException();

        public bool Cancel() =>
            false;

        public bool CancelLegacyPinnedCuda() =>
            false;

        public Task RecoverAsync(CancellationToken ct) =>
            throw new IOException("reconciliation failed");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch (Exception)
            {
                /* Best effort. */
            }
        }
    }
}
