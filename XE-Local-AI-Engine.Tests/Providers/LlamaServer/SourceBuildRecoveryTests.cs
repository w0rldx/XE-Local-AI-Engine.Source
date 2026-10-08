namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

[Category(TestCategories.Unit)]
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
        var logger = new RecordingLogger<LlamaCppSourceBuildService>();
        using var service = CreateService(temp.Path, store, signal, logger);

        await service.RecoverAsync(CancellationToken.None);

        AssertEx.True(Directory.Exists(active));
        AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));
        AssertEx.Equal(GpuVariant.Cpu, signal.ActiveVariant);
        // A wrong working directory makes the script exit 42, which now surfaces as the liveness warning.
        AssertEx.False(logger.HasEntry(LogLevel.Warning, "reported no usable device"));
    }

    /// <summary>
    ///     Booting while CUDA is briefly unusable (driver/library mismatch until reboot, lost WSL passthrough) makes
    ///     <c>--list-devices</c> print no device. That used to read as "the tree is not what the record promises" and
    ///     deleted a valid 20-40 min build.
    /// </summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task Recover_ActiveTreeIdentityMatchesButNoDevice_KeepsTreeAndRecordAndWarns()
    {
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var active = Path.Combine(temp.Path, "llama.cpp", "source-build", "active");
        await SeedTreeAndStateAsync(active, GpuVariant.Cuda, store, GpuVariant.Cuda, reportsDevice: false);
        var logger = new RecordingLogger<LlamaCppSourceBuildService>();
        using var service = CreateService(temp.Path, store, new CudaManagedBuildSignal(), logger);

        await service.RecoverAsync(CancellationToken.None);

        AssertEx.True(File.Exists(Path.Combine(active, "build", "bin", "llama-server")));
        AssertEx.Equal(Path.Combine(active, "build", "bin"), (await store.ReadAsync(CancellationToken.None))?.SourceBuildPath);
        AssertEx.True(logger.HasEntry(LogLevel.Warning, "reported no usable device"));
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Recover_SweepsStaleRuntimeStagingDirectoriesAndKeepsFreshOnes()
    {
        // A kill mid-extract or mid-publish leaves {variant}.{guid}.tmp trees that only a finally block removed.
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var time = new AdvanceableTimeProvider();
        var tagDir = Path.Combine(temp.Path, "llama.cpp", LlamaCppReleasePins.PinnedTag);
        var stale = Path.Combine(tagDir, "vulkan." + Guid.NewGuid().ToString("N") + ".tmp");
        var fresh = Path.Combine(tagDir, "cpu." + Guid.NewGuid().ToString("N") + ".tmp");
        var published = Path.Combine(tagDir, "cpu");

        // A publish renames the OLD runtime aside, and a rename keeps its old mtime: mid-publish on another node it is the
        // only rollback copy, so no age bound may delete it.
        var aside = Path.Combine(tagDir, "cuda." + Guid.NewGuid().ToString("N") + ".aside");
        foreach (var path in new[]
                 {
                     stale,
                     fresh,
                     published,
                     aside
                 })
        {
            Directory.CreateDirectory(path);
        }

        Directory.SetLastWriteTimeUtc(stale, time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(2));
        Directory.SetLastWriteTimeUtc(fresh, time.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(5));
        Directory.SetLastWriteTimeUtc(published, time.GetUtcNow().UtcDateTime - TimeSpan.FromDays(30));
        Directory.SetLastWriteTimeUtc(aside, time.GetUtcNow().UtcDateTime - TimeSpan.FromDays(30));
        using var service = CreateService(temp.Path, store, new CudaManagedBuildSignal(), timeProvider: time);

        await service.RecoverAsync(CancellationToken.None);

        AssertEx.False(Directory.Exists(stale), "a staging tree older than an hour belongs to no live acquisition.");
        AssertEx.True(Directory.Exists(fresh), "a recent staging tree may be another node's live acquisition.");
        AssertEx.True(Directory.Exists(published), "a published variant directory is never a sweep target.");
        AssertEx.True(Directory.Exists(aside), "a publish aside may be a live rollback copy, whatever its mtime.");
    }

    /// <summary>
    ///     Scratch directories hold no state. An undeletable one (a root-owned file from a sudo build attempt) used to
    ///     fail host start for every user, cloud-only ones included; now it boots and only new builds are refused.
    /// </summary>
    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Startup_WhenScratchCleanupFails_BootsAndRefusesNewBuilds()
    {
        if (string.Equals(Environment.UserName, "root", StringComparison.Ordinal))
        {
            Skip.Test("root deletes through a 0500 directory, so the cleanup failure cannot be produced.");
        }

        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var locked = Path.Combine(temp.Path, "llama.cpp", "source-build", ".work", "locked");
        Directory.CreateDirectory(locked);
        await File.WriteAllTextAsync(Path.Combine(locked, "file"), "x");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var buildService = CreateService(temp.Path, store, new CudaManagedBuildSignal());
            var startup = new CudaBuildStartupService(buildService, store, new CudaManagedBuildSignal(), NullLogger<CudaBuildStartupService>.Instance);

            await startup.StartAsync(CancellationToken.None);

            var refusal = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => buildService.StartAsync(new LlamaCppSourceBuildRequest
            {
                Backend = LlamaCppSourceBackend.Cpu,
                Source = LlamaCppSourceSelection.Official
            }, CancellationToken.None));
            AssertEx.Contains(refusal.Message, "scratch directories");

            // Fixing the permissions is enough: every start re-runs recovery, which re-attempts the delete and clears the latch.
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var started = await buildService.StartAsync(new LlamaCppSourceBuildRequest
            {
                Backend = LlamaCppSourceBackend.Cpu,
                Source = LlamaCppSourceSelection.Official
            }, CancellationToken.None);
            // RuntimeBusy comes from the lease step AFTER the scratch check (the test supervisor grants no lease), so no build is launched.
            AssertEx.Equal(LlamaCppSourceBuildStartOutcome.RuntimeBusy, started.Outcome);
            AssertEx.False(Directory.Exists(Path.Combine(temp.Path, "llama.cpp", "source-build", ".work", "locked")));
        }
        finally
        {
            if (Directory.Exists(locked))
            {
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    /// <summary>An active/backup pair that cannot be reconciled is ambiguous runtime state, so it still blocks startup.</summary>
    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Startup_WhenActiveBackupPairCannotBeReconciled_BlocksStartup()
    {
        if (string.Equals(Environment.UserName, "root", StringComparison.Ordinal))
        {
            Skip.Test("root deletes through a 0500 directory, so the reconcile failure cannot be produced.");
        }

        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var sourceRoot = Path.Combine(temp.Path, "llama.cpp", "source-build");
        var backup = Path.Combine(sourceRoot, ".backup");
        await SeedTreeAndStateAsync(Path.Combine(sourceRoot, "active"), GpuVariant.Cpu, store, manifestVariant: GpuVariant.Cpu);
        var locked = Path.Combine(backup, "locked");
        Directory.CreateDirectory(locked);
        await File.WriteAllTextAsync(Path.Combine(locked, "file"), "x");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var buildService = CreateService(temp.Path, store, new CudaManagedBuildSignal());
            var startup = new CudaBuildStartupService(buildService, store, new CudaManagedBuildSignal(), NullLogger<CudaBuildStartupService>.Instance);

            await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => startup.StartAsync(CancellationToken.None));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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
        var logger = new RecordingLogger<CudaBuildStartupService>();
        var startup = new CudaBuildStartupService(buildService,
            store,
            new CudaManagedBuildSignal(),
            logger);
        using var expired = new CancellationTokenSource();
        await expired.CancelAsync();

        await AssertEx.CompletesAsync(startup.StopAsync(expired.Token),
            TestBudgets.Contended,
            "an over-budget shutdown returns instead of rethrowing the host's cancellation.");

        AssertEx.True(logger.HasEntry(LogLevel.Warning, "cut short by the host shutdown budget"),
            "the abandoned drain is absorbed at the hosted-service boundary and reported, not thrown.");
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

        await AssertEx.ThrowsAsync<IOException>(() => service.StartAsync(new LlamaCppSourceBuildRequest
        {
            Backend = LlamaCppSourceBackend.Cpu,
            Source = LlamaCppSourceSelection.Official
        }, CancellationToken.None));

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
        string? resolvedCommit = null,
        bool reportsDevice = true)
    {
        var bin = Path.Combine(tree, "build", "bin");
        var server = WriteServer(bin, variant, requireBinaryWorkingDirectory, reportsDevice);
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

    private static string WriteServer(string bin, GpuVariant variant, bool requireBinaryWorkingDirectory = false, bool reportsDevice = true)
    {
        Directory.CreateDirectory(bin);
        var device = variant == GpuVariant.Cuda ? "CUDA0:" : "Vulkan0:";
        if (!reportsDevice)
        {
            device = "no devices";
        }

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
        ILogger<LlamaCppSourceBuildService>? logger = null,
        TimeProvider? timeProvider = null) =>
        new(new ReadyProbe(), new NoopManager(), store, signal, new LeaseSupervisor(), new LlamaCppSourceBuildActivity(),
            new NullLlamaCppSourceBuildEventPublisher(), logger ?? NullLogger<LlamaCppSourceBuildService>.Instance, timeProvider ?? TimeProvider.System, root);

    private sealed class ReadyProbe : ILlamaCppSourceBuildPrerequisiteProbe
    {
        public Task<LlamaCppSourceBuildPrerequisiteReport> ProbeAsync(LlamaCppSourceBackend backend, CancellationToken ct) =>
            Task.FromResult(new LlamaCppSourceBuildPrerequisiteReport
            {
                CanBuild = true,
                Items = []
            });
    }

    private sealed class NoopManager : ILlamaCppBinaryManager
    {
        public Task<LlamaBinary> EnsureBinaryAsync(GpuVariant variant, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<LlamaBinary?> TryGetInstalledBinaryAsync(GpuVariant variant, CancellationToken ct) =>
            Task.FromResult<LlamaBinary?>(null);

        public Task<LlamaBinary> InstallTagAsync(string tag, string assetName, string digestSha256, long expectedSize, GpuVariant variant, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<InstalledRuntimeState> AdoptCudaSourceBuildAsync(string buildBinDir, string tag, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RemoveSourceBuildAsync(CancellationToken ct) =>
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
