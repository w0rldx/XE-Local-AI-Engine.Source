namespace XE_Local_AI_Engine.Tests.Training.Runs;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The frozen dataset copy holds the same plaintext the encrypted <c>content_json</c> column does, so it is
///     encrypted at rest with the same node key and framing. The decrypted copy under <c>work/</c> is the one plaintext
///     this feature puts on disk — the trainer needs a real file to open — so it is owner-only and swept on every
///     terminal path, failures included.
/// </summary>
public sealed class TrainingRunWorkspaceTests : IDisposable
{
    private readonly FixedNodeSqliteKeyHolder _keyHolder = new(RandomNumberGenerator.GetBytes(32));
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task FrozenCopy_RoundTripsThroughCiphertextIntoOwnerOnlyScratch()
    {
        var workspace = Create();
        var datasetId = Guid.NewGuid();
        var freezeId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var canonical = Encoding.UTF8.GetBytes("""{"sequence":0,"kind":"tool-call","parts":[]}""" + "\n");

        await workspace.WriteFrozenDatasetAsync(datasetId, freezeId, canonical, CancellationToken.None);

        var frozenPath = workspace.FrozenDatasetPath(datasetId, freezeId);
        var onDisk = await File.ReadAllBytesAsync(frozenPath);
        AssertEx.False(Encoding.UTF8.GetString(onDisk).Contains("tool-call", StringComparison.Ordinal),
            "The frozen copy must be ciphertext at rest — it carries the same plaintext as the encrypted sample column.");

        var workPath = await workspace.MaterializeWorkCopyAsync(datasetId, freezeId, runId, CancellationToken.None);

        AssertEx.Equal(Encoding.UTF8.GetString(canonical), await File.ReadAllTextAsync(workPath));
        if (OperatingSystem.IsLinux())
        {
            var mode = File.GetUnixFileMode(workPath);
            AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode, "The decrypted dataset must be readable by its owner only.");
        }
    }

    [Test]
    public async Task WorkDirectory_IsRemovedOnEveryTerminalPath()
    {
        var workspace = Create();
        var datasetId = Guid.NewGuid();
        var freezeId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await workspace.WriteFrozenDatasetAsync(datasetId, freezeId, Encoding.UTF8.GetBytes("{}\n"), CancellationToken.None);
        _ = await workspace.MaterializeWorkCopyAsync(datasetId, freezeId, runId, CancellationToken.None);
        AssertEx.True(Directory.Exists(workspace.WorkDirectory(runId)), "The scratch directory must exist before the sweep.");

        workspace.DeleteWorkDirectory(runId);

        AssertEx.False(Directory.Exists(workspace.WorkDirectory(runId)), "The decrypted scratch must not survive the run.");
        AssertEx.True(File.Exists(workspace.FrozenDatasetPath(datasetId, freezeId)),
            "The ENCRYPTED freeze survives: it is the run's record of what it trained on.");
        // Idempotent: the startup sweep runs over directories a live delete already removed.
        workspace.DeleteWorkDirectory(runId);
    }

    [Test]
    public async Task FrozenCopy_CannotBeDecryptedUnderAnotherFreezeId()
    {
        var workspace = Create();
        var datasetId = Guid.NewGuid();
        var freezeId = Guid.NewGuid();
        var impostorId = Guid.NewGuid();
        await workspace.WriteFrozenDatasetAsync(datasetId, freezeId, Encoding.UTF8.GetBytes("{}\n"), CancellationToken.None);
        File.Copy(workspace.FrozenDatasetPath(datasetId, freezeId), workspace.FrozenDatasetPath(datasetId, impostorId));

        // The associated data binds the blob to its freeze id, so one run's copy cannot be read as another's even
        // when the bytes are moved into place under the other's name.
        _ = await AssertEx.ThrowsAsync<CryptographicException>(() => workspace.MaterializeWorkCopyAsync(datasetId, impostorId, Guid.NewGuid(), CancellationToken.None));
    }

    private TrainingRunWorkspace Create() =>
        new(new FixedNodeDataDirectory(_root), _keyHolder);
}
