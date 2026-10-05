namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.CodexOAuth;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>A Windows-CUDA acquisition publishes its variant dir only once the cudart companion DLLs are inside it.</summary>
/// <remarks>
///     The defect this pins: the build was moved into place first and cudart flattened in afterwards, so a device probe in
///     that window ran a CUDA build that could load no CUDA backend, got an empty GPU list and cached it for the whole
///     session. The HTTP fake records, at the moment the cudart archive is requested, whether the variant dir is
///     already visible. All HTTP is faked — no network.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class BinaryManagerCudartAtomicPublishTests
{
    private const string Tag = "b9799";

    /// <summary>One DLL per family the companion archive delivers, under the CUDA 12 names the pinned archive is assumed to use.</summary>
    private static readonly string[] CompleteCompanionSet = ["cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll"];

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task InstallTag_WindowsCuda_PublishesTheBuildOnlyTogetherWithItsCudartDlls()
    {
        // POSIX only: the post-install smoke test spawns the extracted llama-server.exe, a shell stub with an exec bit.
        using var cache = new TempDir();
        using var fixture = new Fixture(cache.Path, cudartServed: true);
        var manager = fixture.Manager();

        var binary = await manager.InstallTagAsync(Tag, fixture.Pin.AssetName, Sha256Hex(fixture.MainArchive), fixture.MainArchive.LongLength, GpuVariant.Cuda,
            CancellationToken.None);

        AssertEx.Equal(expected: 1, fixture.VariantDirVisibleAtCudartRequest.Count);
        AssertEx.False(fixture.VariantDirVisibleAtCudartRequest[0], "The variant dir must not be published before its cudart DLLs are in place.");
        AssertEx.True(binary.ServerExecutablePath.StartsWith(fixture.VariantDir, StringComparison.Ordinal), "The served binary lives in the published variant dir.");
        AssertCompleteCompanionSet(binary.ServerExecutablePath);
        AssertEx.Empty(fixture.StagingLeftovers());
    }

    [Test]
    public async Task InstallTag_WindowsCuda_CudartFailure_NeverPublishesAHalfCudaDir()
    {
        using var cache = new TempDir();
        using var fixture = new Fixture(cache.Path, cudartServed: false);
        var manager = fixture.Manager();

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.InstallTagAsync(Tag,
            fixture.Pin.AssetName,
            Sha256Hex(fixture.MainArchive),
            fixture.MainArchive.LongLength,
            GpuVariant.Cuda,
            CancellationToken.None));

        // Retried once (two cudart requests), and the variant dir was invisible during both and does not exist after.
        AssertEx.Equal(expected: 2, fixture.VariantDirVisibleAtCudartRequest.Count);
        AssertEx.False(fixture.VariantDirVisibleAtCudartRequest.Any(visible => visible), "The variant dir must not be published before its cudart DLLs are in place.");
        AssertEx.False(Directory.Exists(fixture.VariantDir), "A cudart failure must leave no variant dir behind.");
        AssertEx.Empty(fixture.StagingLeftovers());
    }

    [Test]
    public async Task EnsureBinary_FirstRunWindowsCuda_PublishesTheBuildOnlyTogetherWithItsCudartDlls()
    {
        // The pinned first-run path (no catalog, no record): no smoke test spawns here, so this runs on every OS.
        using var cache = new TempDir();
        using var fixture = new Fixture(cache.Path, cudartServed: true, LlamaCppReleasePins.PinnedTag);

        var binary = await fixture.PinnedManager().EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertEx.Equal(expected: 1, fixture.VariantDirVisibleAtCudartRequest.Count);
        AssertEx.False(fixture.VariantDirVisibleAtCudartRequest[0], "The variant dir must not be published before its cudart DLLs are in place.");
        AssertEx.True(binary.ServerExecutablePath.StartsWith(fixture.VariantDir, StringComparison.Ordinal), "The served binary lives in the published variant dir.");
        AssertCompleteCompanionSet(binary.ServerExecutablePath);
        AssertEx.Empty(fixture.StagingLeftovers());
    }

    [Test]
    public async Task EnsureBinary_FirstRunWindowsCuda_CudartFailure_NeverPublishesAHalfCudaDir()
    {
        using var cache = new TempDir();
        using var fixture = new Fixture(cache.Path, cudartServed: false, LlamaCppReleasePins.PinnedTag);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => fixture.PinnedManager().EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None));

        AssertEx.Equal(expected: 2, fixture.VariantDirVisibleAtCudartRequest.Count);
        AssertEx.False(fixture.VariantDirVisibleAtCudartRequest.Any(visible => visible), "The variant dir must not be published before its cudart DLLs are in place.");
        AssertEx.False(Directory.Exists(fixture.VariantDir), "A cudart failure must leave no variant dir behind.");
        AssertEx.Empty(fixture.StagingLeftovers());
    }

    [Test]
    [Arguments("cublas64_12.dll")]
    [Arguments("cublasLt64_12.dll")]
    public async Task TryGetInstalled_WindowsCudaDirWithTheMarkerButWithoutACublasFamily_ReadsAsNotInstalled(string missingDll)
    {
        // The legacy state: an older build's one-by-one top-up was interrupted after cudart64_* landed. That dir enumerates no GPU, so it is not installed.
        using var cache = new TempDir();
        using var fixture = new Fixture(cache.Path, cudartServed: true, LlamaCppReleasePins.PinnedTag);
        var serverDir = await fixture.WritePublishedServerAsync([.. CompleteCompanionSet.Where(dll => dll != missingDll)]);
        var manager = fixture.PinnedManager();

        AssertEx.Null(await manager.TryGetInstalledBinaryAsync(GpuVariant.Cuda, CancellationToken.None));

        await File.WriteAllTextAsync(Path.Combine(serverDir, missingDll), "fake-cuda-runtime");
        AssertEx.NotNull(await manager.TryGetInstalledBinaryAsync(GpuVariant.Cuda, CancellationToken.None));
    }

    [Test]
    public async Task TryGetInstalled_WindowsVulkanDirWithNoCudaDlls_ReadsAsInstalled()
    {
        // The companion set is a Windows-CUDA requirement only; a Vulkan build next to no CUDA DLL at all is complete.
        using var cache = new TempDir();
        var pin = AssertEx.NotNull(LlamaCppReleasePins.TryResolveExact(OSPlatform.Windows, Architecture.X64, GpuVariant.Vulkan));
        var serverPath = Path.Combine(cache.Path, "llama.cpp", LlamaCppReleasePins.PinnedTag, "vulkan", pin.ServerRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!);
        await File.WriteAllTextAsync(serverPath, "fake-llama-server");
        using var http = new HttpClient();
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Windows, Architecture.X64, TimeProvider.System);

        var binary = AssertEx.NotNull(await manager.TryGetInstalledBinaryAsync(GpuVariant.Vulkan, CancellationToken.None));

        AssertEx.Equal(Path.GetFullPath(serverPath), binary.ServerExecutablePath);
    }

    [Test]
    public async Task EnsureBinary_LegacyPinnedDir_LandsOnlyTheMissingDllsInPlace_AndKeepsEveryFileThatWasThereByteForByte()
    {
        // The operator's Windows state: cudart kept, both cublas families deleted. A cudart of the verified length may be loaded by a running server, so it
        // is never rewritten; this one differs in content from the archive's to prove it.
        using var cache = new TempDir();
        using var fixture = new Fixture(cache.Path, cudartServed: true, LlamaCppReleasePins.PinnedTag);
        var serverDir = await fixture.WritePublishedServerAsync(["ggml-cuda.dll"]);
        await File.WriteAllTextAsync(Path.Combine(serverDir, "cudart64_12.dll"), "real-cudart-12345");
        var before = Snapshot(fixture.VariantDir);
        var registry = new RuntimeAcquisitionStatusRegistry(new NullRuntimeAcquisitionEventPublisher(), NullLogger<RuntimeAcquisitionStatusRegistry>.Instance, TimeProvider.System);
        var manager = fixture.PinnedManager(registry);

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertCompleteCompanionSet(binary.ServerExecutablePath);
        AssertEx.Empty(Lines(before).Except(Lines(Snapshot(fixture.VariantDir))));
        AssertEx.Equal(expected: "real-cudart-12345", await File.ReadAllTextAsync(Path.Combine(serverDir, "cudart64_12.dll")));
        AssertEx.Empty(Directory.EnumerateFiles(fixture.VariantDir, "*.partial", SearchOption.AllDirectories));
        AssertEx.Empty(fixture.StagingLeftovers());
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Completed), registry.Current.Phase);

        await manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertEx.Equal(expected: 1, fixture.VariantDirVisibleAtCudartRequest.Count);
    }

    [Test]
    public async Task EnsureBinary_WhenLandingADllFailsMidway_KeepsEveryFileThatWasThere_FailsNamingTheFile_AndTheNextEnsureCompletesTheSet()
    {
        // A directory where cublasLt64_12.dll must land makes its rename fail on any OS: the in-use or access-denied target of a Windows box.
        using var cache = new TempDir();
        using var fixture = new Fixture(cache.Path, cudartServed: true, LlamaCppReleasePins.PinnedTag);
        var serverDir = await fixture.WritePublishedServerAsync(["cudart64_12.dll", "ggml-cuda.dll"]);
        var obstacle = Directory.CreateDirectory(Path.Combine(serverDir, "cublasLt64_12.dll")).FullName;
        var before = Snapshot(fixture.VariantDir);
        var registry = new RuntimeAcquisitionStatusRegistry(new NullRuntimeAcquisitionEventPublisher(), NullLogger<RuntimeAcquisitionStatusRegistry>.Instance, TimeProvider.System);
        var manager = fixture.PinnedManager(registry);

        var served = await manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertEx.Equal(Path.Combine(serverDir, "llama-server.exe"), served.ServerExecutablePath);
        AssertEx.Empty(Lines(before).Except(Lines(Snapshot(fixture.VariantDir))));
        AssertEx.Empty(Directory.EnumerateFiles(fixture.VariantDir, "*.partial", SearchOption.AllDirectories));
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Failed), registry.Current.Phase);
        var reason = AssertEx.NotNull(registry.Current.SanitizedError);
        AssertEx.True(reason.StartsWith("The CUDA runtime libraries of the installed llama.cpp runtime are incomplete and could not be repaired", StringComparison.Ordinal),
            reason);
        AssertEx.Contains(reason, "cublasLt64_12.dll could not be written to the runtime folder", StringComparison.Ordinal);
        AssertEx.False(reason.Contains(serverDir, StringComparison.Ordinal), "The UI reason names the file, never its path.");

        Directory.Delete(obstacle);
        await manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertCompleteCompanionSet(served.ServerExecutablePath);
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Completed), registry.Current.Phase);
    }

    [Test]
    public async Task EnsureBinary_LegacyDirWithATruncatedCudart_ReplacesItWholeAndThenReadsAsInstalled()
    {
        // An older build copied non-atomically, so an existing DLL may be cut short: a length other than the verified one is replaced by rename.
        using var cache = new TempDir();
        using var fixture = new Fixture(cache.Path, cudartServed: true, LlamaCppReleasePins.PinnedTag);
        var serverDir = await fixture.WritePublishedServerAsync(["cudart64_12.dll"]);
        await File.WriteAllTextAsync(Path.Combine(serverDir, "cudart64_12.dll"), "old-cudart");
        var manager = fixture.PinnedManager();

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertEx.Equal(expected: 1, fixture.VariantDirVisibleAtCudartRequest.Count);
        AssertCompleteCompanionSet(binary.ServerExecutablePath);
        AssertEx.Equal(expected: "fake-cuda-runtime", await File.ReadAllTextAsync(Path.Combine(serverDir, "cudart64_12.dll")));
        AssertEx.Empty(Directory.EnumerateFiles(serverDir, "*.partial"));
        AssertEx.NotNull(await manager.TryGetInstalledBinaryAsync(GpuVariant.Cuda, CancellationToken.None));
    }

    [Test]
    public async Task EnsureBinary_LegacyPinnedDir_WhenTheCompanionDownloadFails_IsLeftByteForByteAndServed_AndTheNextEnsureRepairsIt()
    {
        // Offline: before this check the marker-only dir was served as it was (on the CPU). A failed repair must keep exactly that, and say so.
        using var cache = new TempDir();
        using var fixture = new Fixture(cache.Path, cudartServed: false, LlamaCppReleasePins.PinnedTag);
        var serverDir = await fixture.WritePublishedServerAsync(["cudart64_12.dll", "ggml-cuda.dll"]);
        var before = Snapshot(fixture.VariantDir);
        var registry = new RuntimeAcquisitionStatusRegistry(new NullRuntimeAcquisitionEventPublisher(), NullLogger<RuntimeAcquisitionStatusRegistry>.Instance, TimeProvider.System);
        var logger = new CapturingLogger<LlamaCppBinaryManager>();
        var manager = fixture.PinnedManager(registry, logger);

        var served = await manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertEx.Equal(Path.Combine(serverDir, "llama-server.exe"), served.ServerExecutablePath);
        AssertEx.Equal(before, Snapshot(fixture.VariantDir));
        AssertEx.Empty(fixture.StagingLeftovers());
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Failed), registry.Current.Phase);
        AssertEx.Contains(registry.Current.SanitizedError, "could not be repaired", StringComparison.Ordinal);
        AssertEx.Contains(logger.AllText, "Repairing the incomplete CUDA runtime libraries of llama.cpp", StringComparison.Ordinal);

        fixture.CudartServed = true;
        await manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertEx.Equal(expected: 3, fixture.VariantDirVisibleAtCudartRequest.Count);
        AssertCompleteCompanionSet(served.ServerExecutablePath);
        AssertEx.True(File.Exists(Path.Combine(serverDir, "ggml-cuda.dll")), "The repair keeps every file the dir already had.");
        AssertEx.Empty(fixture.StagingLeftovers());
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Completed), registry.Current.Phase);

        await manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertEx.Equal(expected: 3, fixture.VariantDirVisibleAtCudartRequest.Count);
    }

    [Test]
    public async Task EnsureBinary_AfterAFailedRepair_ASetCompletedElsewhere_ClearsThatFailure_ButNoOtherAcquisitionsFailure()
    {
        // Another node sharing the cache, or the operator, completes the set: this node's cache hit acquires nothing, yet the red banner must not stay forever.
        using var cache = new TempDir();
        using var fixture = new Fixture(cache.Path, cudartServed: false, LlamaCppReleasePins.PinnedTag);
        var serverDir = await fixture.WritePublishedServerAsync(["cudart64_12.dll"]);
        var registry = new RuntimeAcquisitionStatusRegistry(new NullRuntimeAcquisitionEventPublisher(), NullLogger<RuntimeAcquisitionStatusRegistry>.Instance, TimeProvider.System);
        var manager = fixture.PinnedManager(registry);
        await manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Failed), registry.Current.Phase);

        await File.WriteAllTextAsync(Path.Combine(serverDir, "cublas64_12.dll"), "fake-cublas");
        await File.WriteAllTextAsync(Path.Combine(serverDir, "cublasLt64_12.dll"), "fake-cublaslt");
        await manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertEx.Equal(expected: 2, fixture.VariantDirVisibleAtCudartRequest.Count);
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Idle), registry.Current.Phase);

        const string otherFailure = "The llama.cpp runtime could not be downloaded. Check the network connection and try again.";
        registry.Report(new RuntimeAcquisitionUpdate
        {
            Phase = RuntimeAcquisitionPhase.Failed,
            Variant = nameof(GpuVariant.Cuda),
            Tag = LlamaCppReleasePins.PinnedTag,
            StepIndex = 2,
            StepCount = 2,
            SanitizedError = otherFailure
        });
        await manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Failed), registry.Current.Phase);
        AssertEx.Equal(otherFailure, registry.Current.SanitizedError);
        AssertEx.Equal(expected: 2, fixture.VariantDirVisibleAtCudartRequest.Count);
    }

    [Test]
    public async Task EnsureBinary_ACacheHitsClear_NeverOverwritesAStatusAnotherAcquisitionReportedSinceItLooked()
    {
        // Ensures lock per variant dir while every variant and tag shares one registry, so another acquisition can report between a check and its write.
        using var cache = new TempDir();
        using var fixture = new Fixture(cache.Path, cudartServed: false, LlamaCppReleasePins.PinnedTag);
        var serverDir = await fixture.WritePublishedServerAsync(["cudart64_12.dll"]);
        var registry = new InterleavingRegistry(new RuntimeAcquisitionStatusRegistry(new NullRuntimeAcquisitionEventPublisher(),
            NullLogger<RuntimeAcquisitionStatusRegistry>.Instance,
            TimeProvider.System));
        var manager = fixture.PinnedManager(registry);
        await manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Failed), registry.Current.Phase);

        await File.WriteAllTextAsync(Path.Combine(serverDir, "cublas64_12.dll"), "fake-cublas");
        await File.WriteAllTextAsync(Path.Combine(serverDir, "cublasLt64_12.dll"), "fake-cublaslt");
        const string newer = "The llama.cpp runtime could not be downloaded. Check the network connection and try again.";
        registry.BeforeNextAccess = () => registry.Report(new RuntimeAcquisitionUpdate
        {
            Phase = RuntimeAcquisitionPhase.Failed,
            Variant = nameof(GpuVariant.Vulkan),
            Tag = LlamaCppReleasePins.PinnedTag,
            SanitizedError = newer
        });
        await manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Failed), registry.Current.Phase);
        AssertEx.Equal(nameof(GpuVariant.Vulkan), registry.Current.Variant);
        AssertEx.Equal(newer, registry.Current.SanitizedError);
    }

    [Test]
    public async Task EnsureBinary_LegacyDirOfANonPinnedTag_DownloadsNothing_IsLeftByteForByteAndServed_AndWarns()
    {
        // The pin's companion name and digest belong to the pinned release; for any other tag they would fetch the wrong asset, so no repair is attempted.
        using var cache = new TempDir();
        using var fixture = new Fixture(cache.Path, cudartServed: true, Tag);
        var serverDir = await fixture.WritePublishedServerAsync(["cudart64_12.dll"]);
        var before = Snapshot(fixture.VariantDir);
        var logger = new CapturingLogger<LlamaCppBinaryManager>();

        var served = await fixture.PinnedManager(logger: logger).EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertEx.Equal(Path.Combine(serverDir, "llama-server.exe"), served.ServerExecutablePath);
        AssertEx.Empty(fixture.VariantDirVisibleAtCudartRequest);
        AssertEx.Equal(before, Snapshot(fixture.VariantDir));
        AssertEx.Contains(logger.AllText, $"The CUDA runtime libraries of llama.cpp {Tag} are incomplete", StringComparison.Ordinal);
        AssertEx.Contains(logger.AllText, "reinstalling that tag repairs it", StringComparison.Ordinal);
    }

    [Test]
    public async Task InstallTag_WindowsCuda_CompanionArchiveWithoutAFamily_FailsAndPublishesNothing()
    {
        // The operator-install staging path keeps its strict behavior: an incomplete companion discards the staged tree and fails the install.
        using var cache = new TempDir();
        using var fixture = new Fixture(cache.Path, cudartServed: true, companionDlls: ["cudart64_12.dll", "cublas64_12.dll"]);

        var failure = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => fixture.Manager().InstallTagAsync(Tag,
            fixture.Pin.AssetName,
            Sha256Hex(fixture.MainArchive),
            fixture.MainArchive.LongLength,
            GpuVariant.Cuda,
            CancellationToken.None));

        AssertEx.Contains(failure.Message, "no cublasLt64_*.dll next to the server", StringComparison.Ordinal);
        AssertEx.False(Directory.Exists(fixture.VariantDir), "An incomplete companion set must leave no variant dir behind.");
        AssertEx.Empty(fixture.StagingLeftovers());
    }

    [Test]
    public async Task IncompleteCompanionSetMessage_WhenTheListingFails_StillNamesTheMissingFamily()
    {
        using var cache = new TempDir();

        var message = LlamaCppBinaryManager.IncompleteCompanionSetMessage("cublas64_*.dll", Path.Combine(cache.Path, "gone"));

        AssertEx.Contains(message, "no cublas64_*.dll next to the server", StringComparison.Ordinal);
        AssertEx.Contains(message, "DLLs found: could not be listed", StringComparison.Ordinal);
    }

    [Test]
    [Arguments("cublas64_12.dll", "cublas64_*.dll")]
    [Arguments("cublasLt64_12.dll", "cublasLt64_*.dll")]
    public async Task EnsureBinary_FirstRunWindowsCuda_CompanionArchiveWithoutAFamily_FailsNamingItAndPublishesNothing(string omittedDll, string family)
    {
        using var cache = new TempDir();
        using var fixture = new Fixture(cache.Path, cudartServed: true, LlamaCppReleasePins.PinnedTag, [.. CompleteCompanionSet.Where(dll => dll != omittedDll)]);
        var registry = new RuntimeAcquisitionStatusRegistry(new NullRuntimeAcquisitionEventPublisher(), NullLogger<RuntimeAcquisitionStatusRegistry>.Instance, TimeProvider.System);

        var failure = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => fixture.PinnedManager(registry).EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None));

        // The tester's log line must settle the question: which family is missing, and what the archive did deliver.
        AssertEx.Contains(failure.Message, $"no {family} next to the server", StringComparison.Ordinal);
        AssertEx.Contains(failure.Message, "cudart64_12.dll", StringComparison.Ordinal);
        AssertEx.False(Directory.Exists(fixture.VariantDir), "An incomplete companion set must leave no variant dir behind.");
        AssertEx.Empty(fixture.StagingLeftovers());
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Failed), registry.Current.Phase);
        AssertEx.Equal(failure.Message, registry.Current.SanitizedError);
    }

    [Test]
    public async Task FlattenDlls_ATopUpThatFailsMidway_NeverLeavesTheCudartMarkerBehind()
    {
        // The cached-dir top-up flattens into a PUBLISHED dir, where the cudart64_* marker reads as "installed" and "paired". A recursive enumeration yields
        // a dir's own files before its subdirs', so an enumeration-order copy lands the root-level marker before the nested DLL fails, on any filesystem.
        using var cache = new TempDir();
        var source = Directory.CreateDirectory(Path.Combine(cache.Path, "extracted", "bin")).FullName;
        await File.WriteAllTextAsync(Path.Combine(cache.Path, "extracted", "cudart64_12.dll"), "fake-cuda-runtime");
        await File.WriteAllTextAsync(Path.Combine(source, "nvrtc64_120_0.dll"), "fake-nvrtc");
        var serverDir = Directory.CreateDirectory(Path.Combine(cache.Path, "server")).FullName;
        Directory.CreateDirectory(Path.Combine(serverDir, "nvrtc64_120_0.dll"));

        AssertEx.Throws<SystemException>(() => LlamaCppBinaryManager.FlattenDllsInto(Path.Combine(cache.Path, "extracted"), serverDir));

        AssertEx.Empty(Directory.EnumerateFiles(serverDir, "cudart64_*.dll"));
        AssertEx.Empty(Directory.EnumerateFiles(serverDir, "*.partial"));
    }

    [Test]
    public async Task PublishStagedVariant_OverAnExistingVariantDir_ReplacesItAndLeavesNoSiblingBehind()
    {
        using var cache = new TempDir();
        var variantDir = Path.Combine(cache.Path, "llama.cpp", Tag, "cuda");
        Directory.CreateDirectory(variantDir);
        await File.WriteAllTextAsync(Path.Combine(variantDir, "old.dll"), "old");
        var stagingDir = Directory.CreateDirectory($"{variantDir}.staged.tmp").FullName;
        await File.WriteAllTextAsync(Path.Combine(stagingDir, "new.dll"), "new");

        LlamaCppBinaryManager.PublishStagedVariant(stagingDir, variantDir);

        AssertEx.Equal(expected: "new", await File.ReadAllTextAsync(Path.Combine(variantDir, "new.dll")));
        AssertEx.False(File.Exists(Path.Combine(variantDir, "old.dll")), "The old contents must be fully replaced.");
        AssertEx.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(variantDir)!, "cuda.*.tmp"));
    }

    [Test]
    public async Task PublishStagedVariant_WhenTheMoveIntoPlaceFails_LeavesTheExistingVariantDirIntact()
    {
        // A missing staging dir makes the move into place fail after the old dir was already dealt with: it must come back untouched.
        using var cache = new TempDir();
        var variantDir = Path.Combine(cache.Path, "llama.cpp", Tag, "cuda");
        Directory.CreateDirectory(Path.Combine(variantDir, "build", "bin"));
        await File.WriteAllTextAsync(Path.Combine(variantDir, "build", "bin", "llama-server.exe"), "old-server");
        await File.WriteAllTextAsync(Path.Combine(variantDir, "build", "bin", "cudart64_12.dll"), "old-cudart");

        AssertEx.Throws<IOException>(() => LlamaCppBinaryManager.PublishStagedVariant($"{variantDir}.missing.tmp", variantDir));

        AssertEx.Equal(expected: "old-server", await File.ReadAllTextAsync(Path.Combine(variantDir, "build", "bin", "llama-server.exe")));
        AssertEx.Equal(expected: "old-cudart", await File.ReadAllTextAsync(Path.Combine(variantDir, "build", "bin", "cudart64_12.dll")));
        AssertEx.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(variantDir)!, "cuda.*.tmp"));
    }

    private static void AssertCompleteCompanionSet(string serverPath)
    {
        var serverDir = Path.GetDirectoryName(serverPath)!;
        foreach (var dll in CompleteCompanionSet)
        {
            AssertEx.True(File.Exists(Path.Combine(serverDir, dll)), $"{dll} must sit next to the served server.");
        }
    }

    /// <summary>Every file under <paramref name="dir" /> as "relative path: SHA256", sorted, so two snapshots compare names, sizes and contents at once.</summary>
    private static string Snapshot(string dir)
    {
        return string.Join("\n",
            Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(file => $"{Path.GetRelativePath(dir, file)}: {Sha256Hex(File.ReadAllBytes(file))}")
                .Order(StringComparer.Ordinal));
    }

    private static string[] Lines(string snapshot)
    {
        return snapshot.Split('\n');
    }

    private static string Sha256Hex(byte[] bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>Windows-CUDA install inputs plus an HTTP fake that snapshots variant-dir visibility per cudart request.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly ScriptedHandler _handler;
        private readonly HttpClient _http;
        private readonly string _cacheRoot;
        private readonly string _tag;
        private readonly string _cudartName;
        private readonly byte[] _cudartArchive;

        public Fixture(string cacheRoot, bool cudartServed, string tag = Tag, string[]? companionDlls = null)
        {
            _cacheRoot = cacheRoot;
            _tag = tag;
            CudartServed = cudartServed;
            Pin = AssertEx.NotNull(LlamaCppReleasePins.TryResolveExact(OSPlatform.Windows, Architecture.X64, GpuVariant.Cuda));
            _cudartName = AssertEx.NotNull(LlamaCppReleasePins.DeriveCudartAssetName(Pin.AssetName));
            _cudartArchive = BuildZip([.. (companionDlls ?? CompleteCompanionSet).Select(dll => (dll, "fake-cuda-runtime", false))]);
            MainArchive = BuildZip([(Pin.ServerRelativePath, "#!/bin/sh\necho 'version: b9799'\nexit 0\n", true)]);
            VariantDir = Path.Combine(cacheRoot, "llama.cpp", tag, "cuda");
            _handler = new ScriptedHandler(Respond);
            _http = new HttpClient(_handler, disposeHandler: false);
        }

        public LlamaCppAssetPin Pin { get; }

        public byte[] MainArchive { get; }

        public string VariantDir { get; }

        public List<bool> VariantDirVisibleAtCudartRequest { get; } = [];

        /// <summary>Whether the companion request is answered with the archive (else 404); flipped between ensures to model a network that comes back.</summary>
        public bool CudartServed { get; set; }

        public LlamaCppBinaryManager Manager()
        {
            return new LlamaCppBinaryManager(_http,
                _cacheRoot,
                LlamaCppReleasePins.PinnedTag,
                OSPlatform.Windows,
                Architecture.X64,
                TimeProvider.System,
                new CompanionCatalog(_cudartName, Sha256Hex(_cudartArchive), _cudartArchive.LongLength));
        }

        // The real Windows-CUDA pin with its digests swapped for the fake archives', so the pinned path verifies them. The fixture's tag is the active tag.
        public LlamaCppBinaryManager PinnedManager(IRuntimeAcquisitionStatusRegistry? acquisitionStatus = null, ILogger<LlamaCppBinaryManager>? logger = null)
        {
            var pin = new LlamaCppAssetPin
            {
                AssetName = Pin.AssetName,
                Sha256 = Sha256Hex(MainArchive),
                ServerRelativePath = Pin.ServerRelativePath,
                CudartAssetName = _cudartName,
                CudartSha256 = Sha256Hex(_cudartArchive)
            };
            return new LlamaCppBinaryManager(_http,
                _cacheRoot,
                _tag,
                OSPlatform.Windows,
                Architecture.X64,
                TimeProvider.System,
                acquisitionStatus: acquisitionStatus,
                pinResolver: (_, _, _) => pin,
                logger: logger);
        }

        /// <summary>An already-published variant dir: the server stub plus <paramref name="dlls" /> next to it. Returns the server's dir.</summary>
        public async Task<string> WritePublishedServerAsync(string[] dlls)
        {
            var serverPath = Path.Combine(VariantDir, Pin.ServerRelativePath);
            var serverDir = Path.GetDirectoryName(serverPath)!;
            Directory.CreateDirectory(serverDir);
            await File.WriteAllTextAsync(serverPath, "fake-llama-server");
            foreach (var dll in dlls)
            {
                await File.WriteAllTextAsync(Path.Combine(serverDir, dll), "fake-cuda-runtime");
            }

            return serverDir;
        }

        public void Dispose()
        {
            _http.Dispose();
            _handler.Dispose();
        }

        public IEnumerable<string> StagingLeftovers()
        {
            var parent = Path.GetDirectoryName(VariantDir)!;
            return Directory.Exists(parent) ? Directory.EnumerateDirectories(parent, "cuda.*.tmp") : [];
        }

        private static byte[] BuildZip((string Path, string Content, bool Executable)[] entries)
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, content, executable) in entries)
                {
                    var entry = archive.CreateEntry(path);
                    if (executable)
                    {
                        // Unix mode 0755 in the high word; extraction on POSIX restores it so the smoke test can exec the stub.
                        entry.ExternalAttributes = Convert.ToInt32("755", 8) << 16;
                    }

                    using var stream = entry.Open();
                    stream.Write(Encoding.UTF8.GetBytes(content));
                }
            }

            return buffer.ToArray();
        }

        private HttpResponseMessage Respond(Uri uri)
        {
            if (!uri.AbsoluteUri.Contains(_cudartName, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(MainArchive) };
            }

            VariantDirVisibleAtCudartRequest.Add(Directory.Exists(VariantDir));
            return CudartServed
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_cudartArchive) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>A real registry that lets another acquisition report once: just after a caller reads the status, or just before an atomic conditional clear.</summary>
    private sealed class InterleavingRegistry : IRuntimeAcquisitionStatusRegistry
    {
        private readonly IRuntimeAcquisitionStatusRegistry _inner;

        public InterleavingRegistry(IRuntimeAcquisitionStatusRegistry inner)
        {
            _inner = inner;
        }

        public Action? BeforeNextAccess { get; set; }

        public RuntimeAcquisitionStatusEvent Current
        {
            get
            {
                var current = _inner.Current;
                Fire();
                return current;
            }
        }

        public void Report(RuntimeAcquisitionUpdate update)
        {
            _inner.Report(update);
        }

        public bool TryClearFailure(string variant, string tag, string reasonPrefix)
        {
            Fire();
            return _inner.TryClearFailure(variant, tag, reasonPrefix);
        }

        private void Fire()
        {
            var interleave = BeforeNextAccess;
            BeforeNextAccess = null;
            interleave?.Invoke();
        }
    }

    /// <summary>Resolves only the cudart companion; every other lookup reports no live data.</summary>
    private sealed class CompanionCatalog : ILlamaCppReleaseCatalog
    {
        private readonly string _asset;
        private readonly string _digest;
        private readonly long _size;

        public CompanionCatalog(string asset, string digest, long size)
        {
            _asset = asset;
            _digest = digest;
            _size = size;
        }

        public Task<LlamaCppReleaseResult> ResolveRecommendedAsync(string recommendedTag, CancellationToken ct) => Task.FromResult(LlamaCppReleaseResult.Offline());

        public Task<LlamaCppReleaseResult> ResolveUpstreamLatestAsync(CancellationToken ct) => Task.FromResult(LlamaCppReleaseResult.Offline());

        public Task<LlamaCppReleaseResult> ResolveAssetAsync(string tag, OSPlatform os, Architecture arch, GpuVariant variant, CancellationToken ct) =>
            Task.FromResult(LlamaCppReleaseResult.Offline());

        public Task<LlamaCppReleaseResult> ResolveCompanionAssetAsync(string tag, string assetName, CancellationToken ct)
        {
            return Task.FromResult(LlamaCppReleaseResult.ForAsset(tag,
                new LlamaCppReleaseAsset
                {
                    Name = _asset,
                    DownloadUrl = LlamaCppReleasePins.DownloadUri(tag, _asset),
                    Digest = _digest,
                    Size = _size
                }));
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<Uri, HttpResponseMessage> _responder;

        public ScriptedHandler(Func<Uri, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request.RequestUri!));
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-cudart-publish-" + Guid.NewGuid().ToString("N"));
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
