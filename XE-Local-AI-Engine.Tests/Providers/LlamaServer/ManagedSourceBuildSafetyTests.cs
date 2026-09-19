namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

[Category(TestCategories.Unit)]
public sealed class ManagedSourceBuildSafetyTests
{
    [Test]
    [RunOn(OS.Linux)]
    public async Task Adopt_InvalidOfficialProvenance_RejectsBeforePersistence()
    {
        using var temp = new SecureTempDirectory();
        var bin = SeedActiveBin(temp.Path, "#!/bin/sh\nexit 0\n");
        using var store = new InstalledRuntimeStore(temp.Path);

        var manager = CreateManager(temp.Path, store);
        var invalid = new[]
        {
            (Repository: LlamaCppSourceBuildRequestValidation.OfficialRepository,
                Commit: LlamaCppReleasePins.PinnedSourceCommitSha,
                Revision: LlamaCppSourceRevisionMode.DefaultBranch,
                Requested: (string?)null),
            (Repository: "https://github.com/example/fork",
                Commit: LlamaCppReleasePins.PinnedSourceCommitSha,
                Revision: LlamaCppSourceRevisionMode.EnginePinned,
                Requested: (string?)null),
            (Repository: LlamaCppSourceBuildRequestValidation.OfficialRepository,
                Commit: LlamaCppReleasePins.PinnedSourceCommitSha,
                Revision: LlamaCppSourceRevisionMode.EnginePinned,
                Requested: LlamaCppReleasePins.PinnedSourceCommitSha),
            (Repository: LlamaCppSourceBuildRequestValidation.OfficialRepository,
                Commit: new string('a', 40),
                Revision: LlamaCppSourceRevisionMode.EnginePinned,
                Requested: (string?)null)
        };

        foreach (var (repository, commit, revision, requested) in invalid)
        {
            await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.AdoptSourceBuildAsync(bin,
                LlamaCppReleasePins.PinnedTag,
                GpuVariant.Cpu,
                repository,
                commit,
                revision,
                requested,
                LlamaCppSourceSelection.Official,
                CancellationToken.None));
        }

        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Adopt_CustomSelectionUsingOfficialRepository_PersistsExplicitSelection()
    {
        using var temp = new SecureTempDirectory();
        var bin = SeedActiveBin(temp.Path, "#!/bin/sh\nexit 0\n");
        using var store = new InstalledRuntimeStore(temp.Path);

        var state = await CreateManager(temp.Path, store).AdoptSourceBuildAsync(bin,
            LlamaCppReleasePins.PinnedTag,
            GpuVariant.Cpu,
            LlamaCppSourceBuildRequestValidation.OfficialRepository,
            LlamaCppReleasePins.PinnedSourceCommitSha,
            LlamaCppSourceRevisionMode.DefaultBranch,
            null,
            LlamaCppSourceSelection.Custom,
            CancellationToken.None);

        AssertEx.Equal(LlamaCppSourceSelection.Custom, state.SourceSelection);
        AssertEx.Equal(LlamaCppSourceSelection.Custom, (await store.ReadAsync(CancellationToken.None))!.SourceSelection);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Adopt_VulkanRequiresAnchoredBackendDeviceLine()
    {
        using var temp = new SecureTempDirectory();
        var bin = SeedActiveBin(temp.Path, "#!/bin/sh\ncase \"$1\" in --version) exit 0;; --list-devices) echo 'GPU0: generic'; exit 0;; esac\n");
        using var store = new InstalledRuntimeStore(temp.Path);
        var manager = CreateManager(temp.Path, store);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.AdoptSourceBuildAsync(bin, LlamaCppReleasePins.PinnedTag,
            GpuVariant.Vulkan, LlamaCppSourceBuildRequestValidation.OfficialRepository, LlamaCppReleasePins.PinnedSourceCommitSha,
            LlamaCppSourceRevisionMode.EnginePinned, null, CancellationToken.None));
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Adopt_VulkanAcceptsIndexedAnchoredDeviceLine()
    {
        using var temp = new SecureTempDirectory();
        var bin = SeedActiveBin(temp.Path, "#!/bin/sh\ncase \"$1\" in --version) exit 0;; --list-devices) echo '  Vulkan0: test'; exit 0;; esac\n");
        using var store = new InstalledRuntimeStore(temp.Path);
        var manager = CreateManager(temp.Path, store);

        var state = await manager.AdoptSourceBuildAsync(bin, LlamaCppReleasePins.PinnedTag,
            GpuVariant.Vulkan, LlamaCppSourceBuildRequestValidation.OfficialRepository, LlamaCppReleasePins.PinnedSourceCommitSha,
            LlamaCppSourceRevisionMode.EnginePinned, null, CancellationToken.None);

        AssertEx.Equal(GpuVariant.Vulkan, state.Variant);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Adopt_VulkanDeviceValidationRunsFromBinaryDirectory()
    {
        using var temp = new SecureTempDirectory();
        var bin = SeedActiveBin(temp.Path, "#!/bin/sh\ncase \"$1\" in --version) exit 0;; --list-devices) [ -f \"$PWD/runtime.sentinel\" ] || exit 42; echo 'Vulkan0: test'; exit 0;; esac\n");
        await File.WriteAllTextAsync(Path.Combine(bin, "runtime.sentinel"), "present");
        using var store = new InstalledRuntimeStore(temp.Path);

        var state = await CreateManager(temp.Path, store).AdoptSourceBuildAsync(bin, LlamaCppReleasePins.PinnedTag,
            GpuVariant.Vulkan, LlamaCppSourceBuildRequestValidation.OfficialRepository, LlamaCppReleasePins.PinnedSourceCommitSha,
            LlamaCppSourceRevisionMode.EnginePinned, null, CancellationToken.None);

        AssertEx.Equal(GpuVariant.Vulkan, state.Variant);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Adopt_InternalRelativeLibrarySymlink_Accepts()
    {
        using var temp = new SecureTempDirectory();
        var bin = SeedActiveBin(temp.Path, "#!/bin/sh\nexit 0\n");
        await File.WriteAllTextAsync(Path.Combine(bin, "libreal.so"), "library");
        File.CreateSymbolicLink(Path.Combine(bin, "libalias.so"), "libreal.so");
        using var store = new InstalledRuntimeStore(temp.Path);

        var state = await CreateManager(temp.Path, store).AdoptSourceBuildAsync(bin, LlamaCppReleasePins.PinnedTag,
            GpuVariant.Cpu, LlamaCppSourceBuildRequestValidation.OfficialRepository, LlamaCppReleasePins.PinnedSourceCommitSha,
            LlamaCppSourceRevisionMode.EnginePinned, null, CancellationToken.None);

        AssertEx.Equal(GpuVariant.Cpu, state.Variant);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Adopt_EscapingOrCyclicLibrarySymlink_Rejects()
    {
        using var temp = new SecureTempDirectory();
        var bin = SeedActiveBin(temp.Path, "#!/bin/sh\nexit 0\n");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "outside.so"), "outside");
        File.CreateSymbolicLink(Path.Combine(bin, "escape.so"), Path.Combine("..", "..", "..", "..", "..", "outside.so"));
        using var store = new InstalledRuntimeStore(temp.Path);
        var manager = CreateManager(temp.Path, store);
        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.AdoptSourceBuildAsync(bin, LlamaCppReleasePins.PinnedTag,
            GpuVariant.Cpu, LlamaCppSourceBuildRequestValidation.OfficialRepository, LlamaCppReleasePins.PinnedSourceCommitSha,
            LlamaCppSourceRevisionMode.EnginePinned, null, CancellationToken.None));

        File.Delete(Path.Combine(bin, "escape.so"));
        File.CreateSymbolicLink(Path.Combine(bin, "a.so"), "b.so");
        File.CreateSymbolicLink(Path.Combine(bin, "b.so"), "a.so");
        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.AdoptSourceBuildAsync(bin, LlamaCppReleasePins.PinnedTag,
            GpuVariant.Cpu, LlamaCppSourceBuildRequestValidation.OfficialRepository, LlamaCppReleasePins.PinnedSourceCommitSha,
            LlamaCppSourceRevisionMode.EnginePinned, null, CancellationToken.None));
    }

    [Test]
    public async Task RemoveSourceBuild_DeletesOnlyActiveTree()
    {
        using var temp = new SecureTempDirectory();
        var bin = SeedActiveBin(temp.Path, "#!/bin/sh\nexit 0\n");
        var sourceRoot = Path.Combine(temp.Path, "llama.cpp", "source-build");
        var work = Path.Combine(sourceRoot, ".work");
        var staging = Path.Combine(sourceRoot, ".staging");
        var backup = Path.Combine(sourceRoot, ".backup");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(backup);
        using var store = new InstalledRuntimeStore(temp.Path);
        await store.WriteAsync(State(bin, GpuVariant.Cpu, LlamaCppSourceBuildRequestValidation.OfficialRepository), CancellationToken.None);

        await CreateManager(temp.Path, store).RemoveSourceBuildAsync(CancellationToken.None);

        AssertEx.False(Directory.Exists(Path.Combine(sourceRoot, "active")));
        AssertEx.True(Directory.Exists(work));
        AssertEx.True(Directory.Exists(staging));
        AssertEx.True(Directory.Exists(backup));
        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    public async Task RemoveSourceBuild_CustomCudaActiveRuntime_IsRemovedLikeAnyOther()
    {
        // Removal keys off WHERE the record points, never off its provenance: a custom-repository CUDA build in the
        // active tree is the operator's recorded runtime just as much as an official CPU one, and Remove must clear it.
        using var temp = new SecureTempDirectory();
        var bin = SeedActiveBin(temp.Path, "#!/bin/sh\nexit 0\n");
        using var store = new InstalledRuntimeStore(temp.Path);
        await store.WriteAsync(State(bin, GpuVariant.Cuda, "https://github.com/example/custom"), CancellationToken.None);

        await CreateManager(temp.Path, store).RemoveSourceBuildAsync(CancellationToken.None);

        AssertEx.False(Directory.Exists(Path.Combine(temp.Path, "llama.cpp", "source-build", "active")));
        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    public async Task RemoveSourceBuild_PreProvenanceLegacyTree_DeletesOnlyTheExactPinnedTree()
    {
        using var temp = new SecureTempDirectory();
        var legacyTree = Path.Combine(temp.Path, "llama.cpp", "source-cuda", LlamaCppReleasePins.PinnedTag);
        var bin = Path.Combine(legacyTree, "build", "bin");
        Directory.CreateDirectory(bin);
        var server = Path.Combine(bin, "llama-server");
        await File.WriteAllTextAsync(server, "legacy");
        var sibling = Path.Combine(temp.Path, "llama.cpp", "source-cuda", "keep");
        Directory.CreateDirectory(sibling);
        await File.WriteAllTextAsync(Path.Combine(sibling, "sentinel"), "keep");
        using var store = new InstalledRuntimeStore(temp.Path);
        await store.WriteAsync(new InstalledRuntimeState(LlamaCppReleasePins.PinnedTag, "(source-build:cuda)",
            Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(server))), GpuVariant.Cuda,
            DateTimeOffset.UtcNow, bin), CancellationToken.None);

        await CreateManager(temp.Path, store).RemoveSourceBuildAsync(CancellationToken.None);

        AssertEx.False(Directory.Exists(legacyTree));
        AssertEx.True(File.Exists(Path.Combine(sibling, "sentinel")));
        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
    }

    /// <summary>
    ///     A recorded path that merely RESEMBLES the legacy location must never be deleted. The manager compares the
    ///     canonicalized record for exact Ordinal equality against a path it computes itself, so a traversal that lands
    ///     elsewhere, a sibling sharing the pinned tag's leaf spelling, and a directory nested below the real bin
    ///     directory all miss — and take the unrecognised-path branch (record and signal cleared, nothing deleted).
    /// </summary>
    [Test]
    [Arguments("traversal-escapes-the-pinned-tree")]
    [Arguments("sibling-shares-the-pinned-tag-spelling")]
    [Arguments("nested-below-the-pinned-bin-directory")]
    public async Task RemoveSourceBuild_RecordedPathOnlyResemblesTheLegacyTree_DeletesNothing(string deception)
    {
        using var temp = new SecureTempDirectory();
        var sourceCuda = Path.Combine(temp.Path, "llama.cpp", "source-cuda");
        var legacyTree = Path.Combine(sourceCuda, LlamaCppReleasePins.PinnedTag);
        var legacyBin = Path.Combine(legacyTree, "build", "bin");
        Directory.CreateDirectory(legacyBin);
        await File.WriteAllTextAsync(Path.Combine(legacyBin, "llama-server"), "real");

        // The decoy is a REAL directory holding a sentinel, so "nothing was deleted" cannot pass vacuously.
        var (recordedPath, survivingSentinel) = deception switch
        {
            "traversal-escapes-the-pinned-tree" => (Path.Combine(legacyBin, "..", "..", "..", "decoy", "build", "bin"),
                Path.Combine(sourceCuda, "decoy", "build", "bin", "sentinel")),
            "sibling-shares-the-pinned-tag-spelling" => (Path.Combine(sourceCuda, LlamaCppReleasePins.PinnedTag + "-decoy", "build", "bin"),
                Path.Combine(sourceCuda, LlamaCppReleasePins.PinnedTag + "-decoy", "build", "bin", "sentinel")),
            "nested-below-the-pinned-bin-directory" => (Path.Combine(legacyBin, "nested"),
                Path.Combine(legacyBin, "nested", "sentinel")),
            _ => throw new ArgumentOutOfRangeException(nameof(deception), deception, "Unknown deception case.")
        };

        Directory.CreateDirectory(Path.GetFullPath(recordedPath));
        await File.WriteAllTextAsync(survivingSentinel, "keep");
        using var store = new InstalledRuntimeStore(temp.Path);
        await store.WriteAsync(new InstalledRuntimeState(LlamaCppReleasePins.PinnedTag,
            "(source-build:cuda)",
            new string('a', 64),
            GpuVariant.Cuda,
            DateTimeOffset.UtcNow,
            recordedPath), CancellationToken.None);
        var signal = new CudaManagedBuildSignal();
        signal.SetActive(GpuVariant.Cuda);
        var before = signal.Version;

        await CreateManager(temp.Path, store, signal).RemoveSourceBuildAsync(CancellationToken.None);

        AssertEx.True(File.Exists(survivingSentinel), "The decoy the record pointed at was deleted.");
        AssertEx.True(Directory.Exists(legacyTree), "The real pinned tree was deleted although the record never named it.");
        // Unrecognised-path behaviour, unchanged: the record and the cached signal go even though no tree does.
        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
        AssertEx.Null(signal.ActiveVariant);
        AssertEx.True(signal.Version > before);
    }

    [Test]
    public async Task RemoveSourceBuild_NoncanonicalRecordedPath_ClearsStateWithoutDeletingOutsideRoot()
    {
        using var temp = new SecureTempDirectory();
        var outside = Path.Combine(temp.Path, "outside", "build", "bin");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "llama-server");
        await File.WriteAllTextAsync(sentinel, "outside");
        using var store = new InstalledRuntimeStore(temp.Path);
        await store.WriteAsync(new InstalledRuntimeState(LlamaCppReleasePins.PinnedTag,
            "source",
            new string('a', 64),
            GpuVariant.Cpu,
            DateTimeOffset.UtcNow,
            outside), CancellationToken.None);
        var signal = new CudaManagedBuildSignal();
        signal.SetActive(GpuVariant.Cpu);
        var before = signal.Version;

        await CreateManager(temp.Path, store, signal).RemoveSourceBuildAsync(CancellationToken.None);

        AssertEx.True(File.Exists(sentinel));
        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
        AssertEx.Null(signal.ActiveVariant);
        AssertEx.True(signal.Version > before);
    }

    private static InstalledRuntimeState State(string bin, GpuVariant variant, string repository)
    {
        var server = Path.Combine(bin, "llama-server");
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(server)));
        return new InstalledRuntimeState(LlamaCppReleasePins.PinnedTag, "source", sha, variant, DateTimeOffset.UtcNow, bin,
            repository, LlamaCppReleasePins.PinnedSourceCommitSha, LlamaCppSourceRevisionMode.EnginePinned);
    }

    private static LlamaCppBinaryManager CreateManager(string root, IInstalledRuntimeStore store, ICudaManagedBuildSignal? signal = null)
    {
#pragma warning disable CA2000 // Test-scoped manager retains these no-network HTTP resources for the manager lifetime.
        return new LlamaCppBinaryManager(new HttpClient(new ThrowingHandler()), root, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, TimeProvider.System, installedRuntimeStore: store, managedCudaSignal: signal ?? new CudaManagedBuildSignal());
#pragma warning restore CA2000
    }

    private static string SeedActiveBin(string root, string script)
    {
        var bin = Path.Combine(root, "llama.cpp", "source-build", "active", "build", "bin");
        Directory.CreateDirectory(bin);
        var server = Path.Combine(bin, "llama-server");
        File.WriteAllText(server, script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(server, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var directory in new[]
                     {
                         root,
                         Path.Combine(root, "llama.cpp"),
                         Path.Combine(root, "llama.cpp", "source-build"),
                         Path.Combine(root, "llama.cpp", "source-build", "active"),
                         Path.Combine(root, "llama.cpp", "source-build", "active", "build"),
                         bin
                     })
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        return bin;
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
    }

    private sealed class SecureTempDirectory : IDisposable
    {
        public SecureTempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-source-safe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
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
