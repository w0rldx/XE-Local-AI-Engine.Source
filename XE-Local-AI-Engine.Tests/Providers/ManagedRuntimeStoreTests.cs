namespace XE_Local_AI_Engine.Tests.Providers;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The managed-runtime record is a fail-closed tombstone: a corrupt primary degrades to a tombstone carrying the
///     operator's selection, not to "nothing installed", which would let resolution return a prebuilt contradicting the UI.
/// </summary>
/// <remarks>The whisper.cpp and stable-diffusion.cpp stores share one implementation; records on disk read back byte for byte.</remarks>
[Category(TestCategories.Unit)]
public sealed class ManagedRuntimeStoreTests
{
    [Test]
    public async Task WhisperStore_ReadsAndRewritesTheCurrentFileFormatByteForByte()
    {
        using var root = new TempRoot();
        WriteFixture(root.Path, "whisper.cpp", backend: 1,
            WhisperCppSourceBuildRequestValidation.OfficialRepository, WhisperCppReleasePins.PinnedSourceCommitSha);
        var before = ReadFixture(root.Path, "whisper.cpp");
        using var store = new WhisperInstalledRuntimeStore(root.Path);

        var read = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));
        AssertEx.Equal(ManagedRuntimeValidity.Active, read.Validity);
        AssertEx.Equal(WhisperBackend.Cuda, read.DesiredBackend);
        AssertEx.Equal(ManagedSourceSelection.Official, read.SourceSelection);
        await store.WriteAsync(read, CancellationToken.None);

        AssertEx.Equal(before, ReadFixture(root.Path, "whisper.cpp"));
    }

    [Test]
    public async Task ImageStore_ReadsAndRewritesTheCurrentFileFormatByteForByte()
    {
        using var root = new TempRoot();
        WriteFixture(root.Path, "stable-diffusion.cpp", backend: 2,
            StableDiffusionCppSourceBuildRequestValidation.OfficialRepository, StableDiffusionReleasePins.PinnedSourceCommitSha);
        var before = ReadFixture(root.Path, "stable-diffusion.cpp");
        using var store = new StableDiffusionInstalledRuntimeStore(root.Path);

        var read = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));
        AssertEx.Equal(ManagedRuntimeValidity.Active, read.Validity);
        AssertEx.Equal(SdGpuBackend.Vulkan, read.DesiredBackend);
        AssertEx.Equal(ManagedSourceSelection.Official, read.SourceSelection);
        await store.WriteAsync(read, CancellationToken.None);

        AssertEx.Equal(before, ReadFixture(root.Path, "stable-diffusion.cpp"));
    }

    [Test]
    public async Task WhisperStore_RejectsAVulkanBackendTheImageStoreAccepts()
    {
        // whisper.cpp has no Vulkan build, so a record naming backend 2 is not one this node could have written.
        using var root = new TempRoot();
        WriteFixture(root.Path, "whisper.cpp", backend: 2,
            WhisperCppSourceBuildRequestValidation.OfficialRepository, WhisperCppReleasePins.PinnedSourceCommitSha);
        using var store = new WhisperInstalledRuntimeStore(root.Path);

        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    public async Task Write_ThenRead_RoundTripsActiveRecord()
    {
        using var root = new TempRoot();
        using var store = new WhisperInstalledRuntimeStore(root.Path);
        var state = WhisperActiveState(root.Path);

        await store.WriteAsync(state, CancellationToken.None);
        var read = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));

        AssertEx.Equal(ManagedRuntimeValidity.Active, read.Validity);
        AssertEx.Equal(WhisperBackend.Cuda, read.DesiredBackend);
        AssertEx.Equal(AssertEx.NotNull(state.SourceBuildPath), read.SourceBuildPath);
        AssertEx.Equal(AssertEx.NotNull(state.ServerSha256), read.ServerSha256);
        AssertEx.Equal(WhisperCppReleasePins.PinnedSourceCommitSha, read.SourceCommit);
    }

    [Test]
    public async Task Read_NothingWritten_ReturnsNull()
    {
        using var root = new TempRoot();
        using var store = new WhisperInstalledRuntimeStore(root.Path);

        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    public async Task Read_CorruptStateFile_FallsBackToDesiredRecordAsTombstone()
    {
        using var root = new TempRoot();
        using var store = new WhisperInstalledRuntimeStore(root.Path);
        await store.WriteAsync(WhisperActiveState(root.Path), CancellationToken.None);

        // Corrupt the primary only. The redundant desired record still names what the operator selected.
        await File.WriteAllTextAsync(Path.Combine(root.Path, "whisper.cpp", "installed-runtime.json"), "{ not json");

        var read = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));

        AssertEx.Equal(ManagedRuntimeValidity.Invalid, read.Validity);
        AssertEx.Equal(WhisperBackend.Cuda, read.DesiredBackend,
            "The tombstone must still carry the operator's selected backend, or resolution would silently serve a prebuilt.");
        AssertEx.Null(read.SourceBuildPath);
        AssertEx.Null(read.ServerSha256);
        AssertEx.NotNullOrEmpty(read.InvalidReason);
    }

    [Test]
    public async Task Read_SemanticallyInvalidStateFile_DegradesToATombstone()
    {
        using var root = new TempRoot();
        using var store = new WhisperInstalledRuntimeStore(root.Path);
        await store.WriteAsync(WhisperActiveState(root.Path), CancellationToken.None);

        // Structurally valid JSON whose CONTENT is not a valid active record: the build path is gone but the record
        // still claims Active. That must not read back as a usable runtime.
        var statePath = Path.Combine(root.Path, "whisper.cpp", "installed-runtime.json");
        var json = await File.ReadAllTextAsync(statePath);
        await File.WriteAllTextAsync(statePath, json.Replace("\"sourceBuildPath\"", "\"unusedBuildPath\"", StringComparison.Ordinal));

        var read = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));

        AssertEx.Equal(ManagedRuntimeValidity.Invalid, read.Validity);
        AssertEx.NotNullOrEmpty(read.InvalidReason);
    }

    [Test]
    public async Task Write_SemanticallyInvalidState_Throws()
    {
        using var root = new TempRoot();
        using var store = new WhisperInstalledRuntimeStore(root.Path);

        // Active but with no build path and no digest: nothing could ever resolve from it.
        var invalid = WhisperActiveState(root.Path) with
        {
            SourceBuildPath = null,
            ServerSha256 = null
        };

        await AssertEx.ThrowsAsync<ArgumentException>(() => store.WriteAsync(invalid, CancellationToken.None));
    }

    [Test]
    public async Task Write_OfficialSourceWithAForeignCommit_Throws()
    {
        using var root = new TempRoot();
        using var store = new WhisperInstalledRuntimeStore(root.Path);

        // The official selection is pinned by the engine, so a record claiming Official at some other revision is not
        // a record this node could have produced.
        var invalid = WhisperActiveState(root.Path) with
        {
            SourceCommit = new string('b', 40)
        };

        await AssertEx.ThrowsAsync<ArgumentException>(() => store.WriteAsync(invalid, CancellationToken.None));
    }

    [Test]
    public async Task Delete_RemovesBothRecords()
    {
        using var root = new TempRoot();
        using var store = new WhisperInstalledRuntimeStore(root.Path);
        await store.WriteAsync(WhisperActiveState(root.Path), CancellationToken.None);

        await store.DeleteAsync(CancellationToken.None);

        AssertEx.Null(await store.ReadAsync(CancellationToken.None),
            "Deleting must remove the desired record too, or the next read would resurrect a tombstone.");
    }

    [Test]
    public async Task ImageStore_CorruptActiveRecord_ReturnsInvalidDesiredSelectionTombstone()
    {
        using var temp = new TempRoot();
        using var store = new StableDiffusionInstalledRuntimeStore(temp.Path);
        await store.WriteAsync(ImageActiveState(temp.Path), CancellationToken.None);
        var statePath = Path.Combine(temp.Path, "stable-diffusion.cpp", "installed-runtime.json");
        await File.WriteAllTextAsync(statePath, "{broken");

        var recovered = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));

        AssertEx.Equal(ManagedRuntimeValidity.Invalid, recovered.Validity);
        AssertEx.Equal(SdGpuBackend.Cuda, recovered.DesiredBackend);
        AssertEx.Equal(ManagedSourceSelection.Official, recovered.SourceSelection);
    }

    [Test]
    public async Task ImageStore_SemanticallyInvalidEnums_FallBackToDesiredSelectionTombstone()
    {
        using var temp = new TempRoot();
        using var store = new StableDiffusionInstalledRuntimeStore(temp.Path);
        await store.WriteAsync(ImageActiveState(temp.Path), CancellationToken.None);
        var statePath = Path.Combine(temp.Path, "stable-diffusion.cpp", "installed-runtime.json");
        await File.WriteAllTextAsync(statePath,
            $$"""
              {
                "validity": 99,
                "desiredBackend": 99,
                "sourceRepository": null,
                "sourceCommit": "{{StableDiffusionReleasePins.PinnedSourceCommitSha}}",
                "sourceSelection": 99,
                "sourceRevisionMode": 99,
                "installedAtUtc": "{{DateTimeOffset.UtcNow:O}}"
              }
              """);

        var recovered = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));

        AssertEx.Equal(ManagedRuntimeValidity.Invalid, recovered.Validity);
        AssertEx.Equal(SdGpuBackend.Cuda, recovered.DesiredBackend);
        AssertEx.Equal(ManagedSourceSelection.Official, recovered.SourceSelection);
    }

    private static void WriteFixture(string root, string segment, int backend, string repository, string commit)
    {
        // The exact bytes the stores write today: camelCase names, enums as numbers, indented.
        var buildPath = JsonSerializer.Serialize(Path.Combine(root, segment, "managed", "bin"));
        var repositoryJson = JsonSerializer.Serialize(repository);
        var state = $$"""
                      {
                        "validity": 0,
                        "desiredBackend": {{backend}},
                        "sourceRepository": {{repositoryJson}},
                        "sourceCommit": "{{commit}}",
                        "sourceSelection": 0,
                        "sourceRevisionMode": 0,
                        "sourceRequestedCommit": null,
                        "sourceBuildPath": {{buildPath}},
                        "serverSha256": "{{new string('a', 64)}}",
                        "installedAtUtc": "2026-10-28T12:00:00+00:00",
                        "invalidReason": null
                      }
                      """.ReplaceLineEndings(Environment.NewLine);
        var desired = $$"""
                        {
                          "backend": {{backend}},
                          "repository": {{repositoryJson}},
                          "commit": "{{commit}}",
                          "sourceSelection": 0,
                          "revisionMode": 0,
                          "requestedCommit": null,
                          "installedAtUtc": "2026-10-28T12:00:00+00:00"
                        }
                        """.ReplaceLineEndings(Environment.NewLine);
        Directory.CreateDirectory(Path.Combine(root, segment));
        File.WriteAllText(Path.Combine(root, segment, "installed-runtime.json"), state);
        File.WriteAllText(Path.Combine(root, segment, "desired-runtime.json"), desired);
    }

    private static string ReadFixture(string root, string segment) =>
        File.ReadAllText(Path.Combine(root, segment, "installed-runtime.json"))
        + File.ReadAllText(Path.Combine(root, segment, "desired-runtime.json"));

    private static ManagedRuntimeState<WhisperBackend> WhisperActiveState(string root) =>
        new(ManagedRuntimeValidity.Active,
            WhisperBackend.Cuda,
            WhisperCppSourceBuildRequestValidation.OfficialRepository,
            WhisperCppReleasePins.PinnedSourceCommitSha,
            ManagedSourceSelection.Official,
            ManagedSourceRevisionMode.EnginePinned,
            SourceRequestedCommit: null,
            Path.Combine(root, "whisper.cpp", "managed", "bin"),
            new string('a', 64),
            DateTimeOffset.UtcNow);

    private static ManagedRuntimeState<SdGpuBackend> ImageActiveState(string cacheRoot) =>
        new(ManagedRuntimeValidity.Active,
            SdGpuBackend.Cuda,
            StableDiffusionCppSourceBuildRequestValidation.OfficialRepository,
            StableDiffusionReleasePins.PinnedSourceCommitSha,
            ManagedSourceSelection.Official,
            ManagedSourceRevisionMode.EnginePinned,
            SourceRequestedCommit: null,
            Path.Combine(cacheRoot, "stable-diffusion.cpp", "managed", "cuda", StableDiffusionReleasePins.PinnedSourceCommitSha, "build", "bin"),
            new string('a', 64),
            DateTimeOffset.UtcNow);

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-managed-store-test-" + Guid.NewGuid().ToString("N"));
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
