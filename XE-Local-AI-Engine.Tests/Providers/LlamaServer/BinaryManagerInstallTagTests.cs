namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="LlamaCppBinaryManager.InstallTagAsync" />: a digest-matched download installs into the versioned dir,
///     smoke-tests, and records <c>installed-runtime.json</c>; a digest mismatch retries then keeps the old binary and
///     writes NO state; the 3-tier <see cref="LlamaCppBinaryManager.EnsureBinaryAsync" /> still bootstraps from the pins
///     offline. All HTTP is faked — no network. The smoke test spawns a real executable, so these are Linux-only.
/// </summary>
public sealed class BinaryManagerInstallTagTests
{
    private const string Tag = "b9799";
    private const string AssetName = "llama-b9799-bin-ubuntu-x64.tar.gz";

    [Test]
    public async Task InstallTag_WhenSourceRuntimeRecorded_FailsBeforeDownloadAndPreservesRecord()
    {
        using var cache = new TempDir();
        using var handler = new ScriptedHandler(() => throw new InvalidOperationException("A source record must block prebuilt installation."));
        using var http = new HttpClient(handler, disposeHandler: false);
        using var store = new InstalledRuntimeStore(cache.Path);
        var source = new InstalledRuntimeState(LlamaCppReleasePins.PinnedTag,
            "source",
            new string('a', 64),
            GpuVariant.Cpu,
            DateTimeOffset.UtcNow,
            Path.Combine(cache.Path, "llama.cpp", "source-build", "active", "build", "bin"));
        await store.WriteAsync(source, CancellationToken.None);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: store);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.InstallTagAsync(Tag,
            AssetName,
            new string('b', 64),
            expectedSize: 0,
            GpuVariant.Cpu,
            CancellationToken.None));

        AssertEx.Equal(expected: 0, handler.CallCount);
        AssertEx.Equal(source, await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    public async Task InstallTag_AndSourceAdoption_SerializeRecordMutationWithSourceWinningLast()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var cache = new TempDir();
        var archive = BuildExecutableTarGz();
        using var handler = new GatedHandler(archive);
        using var http = new HttpClient(handler, disposeHandler: false);
        using var store = new InstalledRuntimeStore(cache.Path);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: store,
            managedCudaSignal: new CudaManagedBuildSignal());

        var install = manager.InstallTagAsync(Tag, AssetName, Sha256Hex(archive), archive.Length, GpuVariant.Cpu, CancellationToken.None);
        await handler.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        var sourceBin = Path.Combine(cache.Path, "llama.cpp", "source-build", "active", "build", "bin");
        Directory.CreateDirectory(sourceBin);
        var sourceServer = Path.Combine(sourceBin, "llama-server");
        await File.WriteAllTextAsync(sourceServer, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(sourceServer, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var adopt = manager.AdoptSourceBuildAsync(sourceBin,
            LlamaCppReleasePins.PinnedTag,
            GpuVariant.Cpu,
            LlamaCppSourceBuildRequestValidation.OfficialRepository,
            LlamaCppReleasePins.PinnedSourceCommitSha,
            LlamaCppSourceRevisionMode.EnginePinned,
            requestedCommit: null,
            CancellationToken.None);

        await AssertEx.StaysIncompleteAsync(adopt, "The adopt must wait for the in-flight install to release the record lock.");
        handler.Release();
        await install;
        await adopt;

        var installed = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));
        AssertEx.Equal(sourceBin, installed.SourceBuildPath);
    }

    /// <summary>
    ///     The entry guard runs before a download that takes minutes, and the record is shared by every checkout on the
    ///     machine — so another node can adopt a source build while this install is still transferring. The lock is not
    ///     held across the download (that would stall every other node for the whole transfer), so the guard is
    ///     re-checked under it immediately before the write.
    /// </summary>
    [Test]
    public async Task InstallTag_WhenSourceRecordAppearsDuringDownload_RefusesTheWrite()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var cache = new TempDir();
        var archive = BuildExecutableTarGz();
        using var handler = new GatedHandler(archive);
        using var http = new HttpClient(handler, disposeHandler: false);
        using var store = new InstalledRuntimeStore(cache.Path);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: store);

        var install = manager.InstallTagAsync(Tag, AssetName, Sha256Hex(archive), archive.Length, GpuVariant.Cpu, CancellationToken.None);
        await handler.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        // Another node adopts a source build mid-download. It writes through its own store, so this manager's
        // in-process mutation gate never sees it — only the record lock orders the two.
        var source = new InstalledRuntimeState(LlamaCppReleasePins.PinnedTag,
            "source",
            new string('a', 64),
            GpuVariant.Cpu,
            DateTimeOffset.UtcNow,
            Path.Combine(cache.Path, "llama.cpp", "source-build", "active", "build", "bin"));
        await store.WriteAsync(source, CancellationToken.None);
        handler.Release();

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => install);
        AssertEx.Equal(source, await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    public async Task InstallTag_WhenDigestMatches_AtomicallyInstallsAndWritesState()
    {
        // The smoke test spawns the extracted llama-server (here a POSIX shell stub). On Windows the stub is not
        // executable, so the spawn semantics differ — exercise the install+state contract on POSIX hosts only.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var cache = new TempDir();
        var archive = BuildExecutableTarGz();
        var digest = Sha256Hex(archive);

        using var handler = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archive)
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        using var store = new InstalledRuntimeStore(cache.Path);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: store);

        var binary = await manager.InstallTagAsync(Tag, AssetName, $"sha256:{digest}", archive.Length, GpuVariant.Cpu, CancellationToken.None);

        AssertEx.Equal(Tag, binary.Version);
        AssertEx.False(binary.IsPinnedFallback);
        AssertEx.True(File.Exists(binary.ServerExecutablePath));

        var state = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));
        AssertEx.Equal(Tag, state.Tag);
        AssertEx.Equal(AssetName, state.Asset);
    }

    [Test]
    public async Task InstallTag_WhenDigestMismatch_RetriesThenKeepsOldBinary_NoStateWritten()
    {
        using var cache = new TempDir();
        // A pre-existing pinned binary that must survive the failed upgrade.
        var pinnedServer = Path.Combine(cache.Path, "llama.cpp", LlamaCppReleasePins.PinnedTag, "cpu", "build", "bin", "llama-server");
        Directory.CreateDirectory(Path.GetDirectoryName(pinnedServer)!);
        await File.WriteAllTextAsync(pinnedServer, "pinned-binary");

        using var handler = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("bytes-that-do-not-match-the-digest"u8.ToArray())
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        using var store = new InstalledRuntimeStore(cache.Path);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: store);

        var digest = new string('f', 64);
        var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.InstallTagAsync(Tag, AssetName, digest, expectedSize: 0, GpuVariant.Cpu, CancellationToken.None));

        // Retried exactly once (two attempts) and surfaced sanitized.
        AssertEx.Equal(expected: 2, handler.CallCount);
        AssertEx.False(exception.Message.Contains(cache.Path, StringComparison.Ordinal));
        // The pinned binary is intact and NO install state was written.
        AssertEx.True(File.Exists(pinnedServer));
        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    public async Task InstallTag_RejectsBadTag_NoDownload()
    {
        using var cache = new TempDir();
        using var handler = new ScriptedHandler(() => throw new InvalidOperationException("A malformed tag must never download."));
        using var http = new HttpClient(handler, disposeHandler: false);
        using var store = new InstalledRuntimeStore(cache.Path);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: store);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.InstallTagAsync("../escape", AssetName, new string('a', 64), expectedSize: 0, GpuVariant.Cpu, CancellationToken.None));

        AssertEx.Equal(expected: 0, handler.CallCount);
    }

    [Test]
    public async Task InstallTag_RejectsAssetNameWithTraversal_NoDownload()
    {
        using var cache = new TempDir();
        using var handler = new ScriptedHandler(() => throw new InvalidOperationException("A traversal asset name must never download."));
        using var http = new HttpClient(handler, disposeHandler: false);
        using var store = new InstalledRuntimeStore(cache.Path);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: store);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.InstallTagAsync(Tag, "../../etc/passwd", new string('a', 64), expectedSize: 0, GpuVariant.Cpu, CancellationToken.None));
        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.InstallTagAsync(Tag, "sub/dir/asset.tar.gz", new string('a', 64), expectedSize: 0, GpuVariant.Cpu, CancellationToken.None));

        AssertEx.Equal(expected: 0, handler.CallCount);
    }

    [Test]
    public async Task InstallTag_WhenExpectedSizeExceedsCeiling_RejectedBeforeDownload()
    {
        using var cache = new TempDir();
        using var handler = new ScriptedHandler(() => throw new InvalidOperationException("An oversized asset must never download."));
        using var http = new HttpClient(handler, disposeHandler: false);
        using var store = new InstalledRuntimeStore(cache.Path);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: store);

        // 3 GiB > the 2 GiB absolute ceiling.
        var oversized = 3L * 1024 * 1024 * 1024;
        var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.InstallTagAsync(Tag, AssetName, new string('a', 64), oversized, GpuVariant.Cpu, CancellationToken.None));

        AssertEx.Equal(expected: 0, handler.CallCount);
        AssertEx.False(exception.Message.Contains(cache.Path, StringComparison.Ordinal));
    }

    [Test]
    public async Task InstallTag_WhenStreamExceedsBound_AbortsAndCleansTemp_NoState()
    {
        using var cache = new TempDir();
        // A unique asset name so this test's GUID-tagged temp archives can be isolated from any concurrent test's.
        var uniqueAsset = $"llama-{Guid.NewGuid():N}-oversize.tar.gz";
        // Server streams far more than the slack allows for a tiny declared size → the bounded copy must abort.
        var body = new byte[4 * 1024 * 1024];
        using var handler = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        using var store = new InstalledRuntimeStore(cache.Path);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: store);

        var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() =>
            manager.InstallTagAsync(Tag, uniqueAsset, new string('a', 64), expectedSize: 1024, GpuVariant.Cpu, CancellationToken.None));

        AssertEx.False(exception.Message.Contains(cache.Path, StringComparison.Ordinal));
        // The aborted download left no temp archive for THIS unique asset behind, and no install state was recorded.
        var leaked = SnapshotTempArchives().Where(path => path.EndsWith(uniqueAsset, StringComparison.Ordinal)).ToList();
        AssertEx.Equal(expected: 0, leaked.Count);
        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    public async Task InstallTag_WhenOnDiskSizeMismatchesExpected_FailsAndKeepsOld()
    {
        using var cache = new TempDir();
        var archive = BuildExecutableTarGz();
        var digest = Sha256Hex(archive);

        using var handler = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archive)
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        using var store = new InstalledRuntimeStore(cache.Path);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: store);

        // Declare a size that does not equal the actual archive length → the post-download length check must fail.
        var wrongSize = archive.Length + 1;
        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.InstallTagAsync(Tag, AssetName, $"sha256:{digest}", wrongSize, GpuVariant.Cpu, CancellationToken.None));

        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    public async Task InstallTag_WhenSmokeTestFails_RemovesExtractedVariantDir_NoState()
    {
        // POSIX only: the smoke test spawns the extracted server. Here the extracted "server" exits non-zero, failing
        // the self-check; the just-extracted variant dir must be removed so a later resolve can't serve it unverified.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var cache = new TempDir();
        var archive = BuildFailingServerTarGz();
        var digest = Sha256Hex(archive);

        using var handler = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archive)
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        using var store = new InstalledRuntimeStore(cache.Path);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: store);

        var variantDir = Path.Combine(cache.Path, "llama.cpp", Tag, "cpu");
        var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.InstallTagAsync(Tag, AssetName, $"sha256:{digest}", archive.Length, GpuVariant.Cpu, CancellationToken.None));

        AssertEx.False(exception.Message.Contains(cache.Path, StringComparison.Ordinal));
        AssertEx.False(Directory.Exists(variantDir));
        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    public async Task InstallTag_RejectsMissingDigest_NoDownload()
    {
        using var cache = new TempDir();
        using var handler = new ScriptedHandler(() => throw new InvalidOperationException("A missing digest must never download."));
        using var http = new HttpClient(handler, disposeHandler: false);
        using var store = new InstalledRuntimeStore(cache.Path);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: store);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.InstallTagAsync(Tag, AssetName, digestSha256: "", expectedSize: 0, GpuVariant.Cpu, CancellationToken.None));

        AssertEx.Equal(expected: 0, handler.CallCount);
    }

    [Test]
    public async Task EnsureBinary_OfflineFirstRun_BootstrapsFromPins()
    {
        // No state file, the catalog reports offline, and the pinned archive is already extracted on disk → tier-3
        // (pins) bootstrap must succeed with zero downloads (the brand-new offline first-run contract).
        using var cache = new TempDir();
        var pin = LlamaCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu)!;
        var serverPath = Path.Combine(cache.Path, "llama.cpp", LlamaCppReleasePins.PinnedTag, "cpu", pin.ServerRelativePath.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!);
        await File.WriteAllTextAsync(serverPath, "pinned-binary");

        using var handler = new ScriptedHandler(() => throw new InvalidOperationException("Offline bootstrap must not download."));
        using var http = new HttpClient(handler, disposeHandler: false);
        using var store = new InstalledRuntimeStore(cache.Path);
        var catalog = new OfflineCatalog();
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog, store);

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None);

        AssertEx.Equal(expected: 0, handler.CallCount);
        AssertEx.Equal(LlamaCppReleasePins.PinnedTag, binary.Version);
        AssertEx.True(binary.IsPinnedFallback);
        AssertEx.Equal(serverPath, binary.ServerExecutablePath);
    }

    [Test]
    public async Task EnsureBinary_WhenInstalledStatePresent_PrefersInstalledTagOffline()
    {
        // Tier 2: with the live catalog offline, the recorded installed tag is used (not the pinned floor).
        using var cache = new TempDir();
        var installedServer = Path.Combine(cache.Path, "llama.cpp", Tag, "cpu", "build", "bin", "llama-server");
        Directory.CreateDirectory(Path.GetDirectoryName(installedServer)!);
        await File.WriteAllTextAsync(installedServer, "installed-binary");

        using var store = new InstalledRuntimeStore(cache.Path);
        await store.WriteAsync(new InstalledRuntimeState(Tag, AssetName, new string('a', 64), GpuVariant.Cpu, DateTimeOffset.UtcNow), CancellationToken.None);

        using var handler = new ScriptedHandler(() => throw new InvalidOperationException("Cached reuse must not download."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, new OfflineCatalog(), store);

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None);

        AssertEx.Equal(expected: 0, handler.CallCount);
        AssertEx.Equal(Tag, binary.Version);
        AssertEx.False(binary.IsPinnedFallback);
    }

    [Test]
    public async Task EnsureBinary_OnFreshNode_RecordsResolvedRuntimeSoItSurfacesAsInstalled()
    {
        // Issue #1: a pin-bootstrapped binary with NO installed-runtime record must be recorded on ensure so the
        // runtime-status surface shows "Installed: <tag> (<variant>)" on first load (no explicit update ever ran).
        using var cache = new TempDir();
        var pin = LlamaCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu)!;
        var serverPath = Path.Combine(cache.Path, "llama.cpp", LlamaCppReleasePins.PinnedTag, "cpu", pin.ServerRelativePath.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!);
        await File.WriteAllTextAsync(serverPath, "pinned-binary");

        using var handler = new ScriptedHandler(() => throw new InvalidOperationException("Cached bootstrap must not download."));
        using var http = new HttpClient(handler, disposeHandler: false);
        using var store = new InstalledRuntimeStore(cache.Path);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, new OfflineCatalog(), store);

        AssertEx.Null(await store.ReadAsync(CancellationToken.None));

        await manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None);

        var state = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));
        AssertEx.Equal(LlamaCppReleasePins.PinnedTag, state.Tag);
        AssertEx.Equal(GpuVariant.Cpu, state.Variant);
        AssertEx.Equal(pin.AssetName, state.Asset);
        AssertEx.Equal(expected: 0, handler.CallCount);
    }

    [Test]
    public async Task EnsureBinary_WhenRecordAlreadyMatches_DoesNotRewriteState()
    {
        // The hot path must not rewrite an identical record on every ensure: a record for the same (tag, variant) the
        // ensure resolves is left untouched (the InstalledAtUtc stamp must not advance).
        using var cache = new TempDir();
        var pin = LlamaCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu)!;
        var serverPath = Path.Combine(cache.Path, "llama.cpp", LlamaCppReleasePins.PinnedTag, "cpu", pin.ServerRelativePath.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!);
        await File.WriteAllTextAsync(serverPath, "pinned-binary");

        using var store = new InstalledRuntimeStore(cache.Path);
        var recordedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);
        await store.WriteAsync(new InstalledRuntimeState(LlamaCppReleasePins.PinnedTag, pin.AssetName, new string('a', 64), GpuVariant.Cpu, recordedAt), CancellationToken.None);

        using var handler = new ScriptedHandler(() => throw new InvalidOperationException("Cached reuse must not download."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, new OfflineCatalog(), store);

        await manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None);

        var state = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));
        // The original digest + timestamp survive — the steady-state ensure performed no write.
        AssertEx.Equal(new string('a', 64), state.Sha256);
        AssertEx.Equal(recordedAt.ToUnixTimeMilliseconds(), state.InstalledAtUtc.ToUnixTimeMilliseconds());
    }

    [Test]
    public async Task EnsureBinary_WhenResolveIsPinnedFloorButNewerRecordExists_DoesNotClobberRecord()
    {
        // The pinned-floor branch must never overwrite a newer explicit-install record for the same variant. Here the
        // live catalog resolves the pinned floor (e.g. a node whose recommended setting points back at the pin) while a
        // newer tag is already recorded — the recorded tag must survive (only the live/tier-2 resolve may advance it).
        using var cache = new TempDir();
        const string newerTag = "b9799";
        AssertEx.False(string.Equals(newerTag, LlamaCppReleasePins.PinnedTag, StringComparison.Ordinal), "The recorded tag must differ from the pinned floor for this test to be meaningful.");

        // The pinned-floor binary is cached on disk so the resolve serves it without a download.
        var pin = LlamaCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu)!;
        var pinnedServer = Path.Combine(cache.Path, "llama.cpp", LlamaCppReleasePins.PinnedTag, "cpu", pin.ServerRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(pinnedServer)!);
        await File.WriteAllTextAsync(pinnedServer, "pinned-binary");

        using var store = new InstalledRuntimeStore(cache.Path);
        await store.WriteAsync(new InstalledRuntimeState(newerTag, AssetName, new string('b', 64), GpuVariant.Cpu, DateTimeOffset.UtcNow), CancellationToken.None);

        using var handler = new ScriptedHandler(() => throw new InvalidOperationException("Cached resolve must not download."));
        using var http = new HttpClient(handler, disposeHandler: false);
        // A catalog that live-resolves the pinned floor (tier 1) → the resolved tag becomes the pinned floor.
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, new ResolvesPinnedFloorCatalog(), store);

        await manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None);

        AssertEx.Equal(expected: 0, handler.CallCount);
        // The record still points at the newer explicit install — the pinned floor never clobbered it.
        var state = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));
        AssertEx.Equal(newerTag, state.Tag);
        AssertEx.Equal(new string('b', 64), state.Sha256);
    }

    [Test]
    public async Task EnsureBinary_CrossVariantNonPinnedResolve_DoesNotOverwriteRecordAssetOrSha()
    {
        // MED-1 record integrity: an ensure for a DIFFERENT variant that resolves a non-pinned tier-2 tag (the recorded
        // higher tag) must NOT write the pin's asset/sha (which belong to a different variant + the pinned floor) over
        // the authoritative record. The pin is resolved by OS/arch/variant, so its asset/digest never match an arbitrary
        // non-pinned tag — recording them would corrupt Tag↔Asset↔Sha256 consistency.
        using var cache = new TempDir();
        const string higherTag = "b9700";
        AssertEx.False(string.Equals(higherTag, LlamaCppReleasePins.PinnedTag, StringComparison.Ordinal), "The recorded tag must be a non-pinned (explicitly-installed) tag for this test.");

        // The record was written by a prior Vulkan InstallTagAsync: tag b9700, the Vulkan asset, a Vulkan digest.
        var vulkanPin = LlamaCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, GpuVariant.Vulkan)!;
        var recordedAsset = vulkanPin.AssetName;
        var recordedSha = new string('c', 64);
        using var store = new InstalledRuntimeStore(cache.Path);
        await store.WriteAsync(new InstalledRuntimeState(higherTag, recordedAsset, recordedSha, GpuVariant.Vulkan, DateTimeOffset.UtcNow), CancellationToken.None);

        // A cached Cpu binary for the higher tag so the Cpu ensure resolves (tier 2 → higherTag) and reuses it offline.
        var cpuServer = Path.Combine(cache.Path, "llama.cpp", higherTag, "cpu", "build", "bin", "llama-server");
        Directory.CreateDirectory(Path.GetDirectoryName(cpuServer)!);
        await File.WriteAllTextAsync(cpuServer, "cpu-binary");

        using var handler = new ScriptedHandler(() => throw new InvalidOperationException("Cached reuse must not download."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, new OfflineCatalog(), store);

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None);

        AssertEx.Equal(expected: 0, handler.CallCount);
        AssertEx.Equal(higherTag, binary.Version);
        // The record is byte-for-byte unchanged — no pinned/cpu asset or digest leaked into it.
        var state = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));
        AssertEx.Equal(higherTag, state.Tag);
        AssertEx.Equal(GpuVariant.Vulkan, state.Variant);
        AssertEx.Equal(recordedAsset, state.Asset);
        AssertEx.Equal(recordedSha, state.Sha256);
    }

    private static byte[] BuildExecutableTarGz()
    {
        using var raw = new MemoryStream();
        using (var tar = new TarWriter(raw, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "build/bin/llama-server")
            {
                // POSIX exec bits so the spawned smoke test can run it.
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                DataStream = new MemoryStream("#!/bin/sh\necho 'version: b9799'\nexit 0\n"u8.ToArray())
            };
            tar.WriteEntry(entry);
        }

        raw.Position = 0;
        using var gz = new MemoryStream();
        using (var gzip = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true))
        {
            raw.CopyTo(gzip);
        }

        return gz.ToArray();
    }

    private static byte[] BuildFailingServerTarGz()
    {
        using var raw = new MemoryStream();
        using (var tar = new TarWriter(raw, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "build/bin/llama-server")
            {
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                // Exits non-zero → the smoke test reports a failed self-check.
                DataStream = new MemoryStream("#!/bin/sh\nexit 1\n"u8.ToArray())
            };
            tar.WriteEntry(entry);
        }

        raw.Position = 0;
        using var gz = new MemoryStream();
        using (var gzip = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true))
        {
            raw.CopyTo(gzip);
        }

        return gz.ToArray();
    }

    private static HashSet<string> SnapshotTempArchives()
    {
        return [.. Directory.EnumerateFiles(Path.GetTempPath(), "llamacpp-*")];
    }

    private static string Sha256Hex(byte[] bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private sealed class OfflineCatalog : ILlamaCppReleaseCatalog
    {
        public Task<LlamaCppReleaseResult> ResolveRecommendedAsync(string recommendedTag, CancellationToken ct)
        {
            return Task.FromResult(LlamaCppReleaseResult.Offline());
        }

        public Task<LlamaCppReleaseResult> ResolveUpstreamLatestAsync(CancellationToken ct)
        {
            return Task.FromResult(LlamaCppReleaseResult.Offline());
        }

        public Task<LlamaCppReleaseResult> ResolveAssetAsync(string tag, OSPlatform os, Architecture arch, GpuVariant variant, CancellationToken ct)
        {
            return Task.FromResult(LlamaCppReleaseResult.Offline());
        }

        public Task<LlamaCppReleaseResult> ResolveCompanionAssetAsync(string tag, string assetName, CancellationToken ct)
        {
            return Task.FromResult(LlamaCppReleaseResult.Offline());
        }
    }

    /// <summary>Catalog that live-resolves the pinned floor tag (tier 1) so the resolve lands on the pin with a live result.</summary>
    private sealed class ResolvesPinnedFloorCatalog : ILlamaCppReleaseCatalog
    {
        public Task<LlamaCppReleaseResult> ResolveRecommendedAsync(string recommendedTag, CancellationToken ct)
        {
            return Task.FromResult(LlamaCppReleaseResult.ForTag(LlamaCppReleasePins.PinnedTag));
        }

        public Task<LlamaCppReleaseResult> ResolveUpstreamLatestAsync(CancellationToken ct)
        {
            return Task.FromResult(LlamaCppReleaseResult.Offline());
        }

        public Task<LlamaCppReleaseResult> ResolveAssetAsync(string tag, OSPlatform os, Architecture arch, GpuVariant variant, CancellationToken ct)
        {
            return Task.FromResult(LlamaCppReleaseResult.ForTag(LlamaCppReleasePins.PinnedTag));
        }

        public Task<LlamaCppReleaseResult> ResolveCompanionAssetAsync(string tag, string assetName, CancellationToken ct)
        {
            return Task.FromResult(LlamaCppReleaseResult.Offline());
        }
    }

    private sealed class ScriptedHandler(Func<HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responder());
        }
    }

    private sealed class GatedHandler(byte[] content) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() =>
            _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-installtag-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
