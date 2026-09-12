namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The managed-runtime record is a fail-closed tombstone as much as it is a success record. These cases pin that:
///     a corrupt primary degrades to a tombstone carrying the operator's selection rather than to "nothing installed",
///     which would let resolution hand back a prebuilt that contradicts the UI.
/// </summary>
public sealed class WhisperInstalledRuntimeStoreTests
{
    [Test]
    public async Task Write_ThenRead_RoundTripsActiveRecord()
    {
        using var root = new TempRoot();
        using var store = new WhisperInstalledRuntimeStore(root.Path);
        var state = ActiveState(root.Path);

        await store.WriteAsync(state, CancellationToken.None);
        var read = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));

        AssertEx.Equal(WhisperInstalledRuntimeValidity.Active, read.Validity);
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
        await store.WriteAsync(ActiveState(root.Path), CancellationToken.None);

        // Corrupt the primary only. The redundant desired record still names what the operator selected.
        await File.WriteAllTextAsync(Path.Combine(root.Path, "whisper.cpp", "installed-runtime.json"), "{ not json");

        var read = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));

        AssertEx.Equal(WhisperInstalledRuntimeValidity.Invalid, read.Validity);
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
        await store.WriteAsync(ActiveState(root.Path), CancellationToken.None);

        // Structurally valid JSON whose CONTENT is not a valid active record: the build path is gone but the record
        // still claims Active. That must not read back as a usable runtime.
        var statePath = Path.Combine(root.Path, "whisper.cpp", "installed-runtime.json");
        var json = await File.ReadAllTextAsync(statePath);
        await File.WriteAllTextAsync(statePath, json.Replace("\"sourceBuildPath\"", "\"unusedBuildPath\"", StringComparison.Ordinal));

        var read = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));

        AssertEx.Equal(WhisperInstalledRuntimeValidity.Invalid, read.Validity);
        AssertEx.NotNullOrEmpty(read.InvalidReason);
    }

    [Test]
    public async Task Write_SemanticallyInvalidState_Throws()
    {
        using var root = new TempRoot();
        using var store = new WhisperInstalledRuntimeStore(root.Path);

        // Active but with no build path and no digest: nothing could ever resolve from it.
        var invalid = ActiveState(root.Path) with
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
        var invalid = ActiveState(root.Path) with
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
        await store.WriteAsync(ActiveState(root.Path), CancellationToken.None);

        await store.DeleteAsync(CancellationToken.None);

        AssertEx.Null(await store.ReadAsync(CancellationToken.None),
            "Deleting must remove the desired record too, or the next read would resurrect a tombstone.");
    }

    private static WhisperInstalledRuntimeState ActiveState(string root) =>
        new(WhisperInstalledRuntimeValidity.Active,
            WhisperBackend.Cuda,
            WhisperCppSourceBuildRequestValidation.OfficialRepository,
            WhisperCppReleasePins.PinnedSourceCommitSha,
            WhisperCppSourceSelection.Official,
            WhisperCppSourceRevisionMode.EnginePinned,
            SourceRequestedCommit: null,
            Path.Combine(root, "whisper.cpp", "managed", "bin"),
            new string('a', 64),
            DateTimeOffset.UtcNow);

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-whisper-store-test-" + Guid.NewGuid().ToString("N"));
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
