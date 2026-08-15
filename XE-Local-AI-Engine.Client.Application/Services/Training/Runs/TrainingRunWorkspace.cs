namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Owns every path a run touches, and the encrypt/decrypt of the frozen dataset copy.
/// </summary>
/// <remarks>
///     <para>
///         The frozen copy is the run's own snapshot of the dataset, written once at creation and never re-read from
///         the live tables again — that is what makes a sample edit after enqueue provably unable to change what a run
///         trained on. It is encrypted at rest with the same node key and framing as every other on-disk blob
///         (<c>nonce || ciphertext || tag</c>, AAD bound to the run id and a column name), because it holds the same
///         plaintext the encrypted <c>content_json</c> column does.
///     </para>
///     <para>
///         The DECRYPTED copy under <c>work/</c> is the one thing here that is plaintext on disk, so it is written
///         owner-only and deleted on every terminal path including failure — the trainer needs a real file to open and
///         a Python subprocess cannot be handed a decrypted byte array.
///     </para>
/// </remarks>
public sealed class TrainingRunWorkspace
{
    /// <summary>AAD column name for the frozen dataset blob. Binds the blob to its role under the shared node key.</summary>
    private const string FrozenDatasetColumn = "training_frozen_dataset";

    private readonly UploadedFileBlobProtector _protector;
    private readonly INodeDataDirectory _dataDirectory;

    public TrainingRunWorkspace(INodeDataDirectory dataDirectory, INodeSqliteKeyHolder keyHolder)
    {
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        // No shared singleton exists — every on-disk blob consumer constructs its own, as KnowledgeDocumentBlobStore does.
        _protector = new UploadedFileBlobProtector(keyHolder ?? throw new ArgumentNullException(nameof(keyHolder)));
    }

    public string TrainingRoot =>
        Path.Combine(_dataDirectory.Root, "training");

    public string RunsRoot =>
        Path.Combine(TrainingRoot, "runs");

    public string RunRoot(Guid runId) =>
        Path.Combine(RunsRoot, runId.ToString());

    /// <summary>Scratch the trainer reads and writes, including the decrypted dataset. Deleted on every terminal path.</summary>
    public string WorkDirectory(Guid runId) =>
        Path.Combine(RunRoot(runId), "work");

    /// <summary>Where the trainer writes its adapter. Survives the run; promotion moves it into the registry.</summary>
    public string StagedDirectory(Guid runId) =>
        Path.Combine(RunRoot(runId), "staged");

    /// <summary>
    ///     Keyed by the FREEZE id rather than the run id: the store generates the run id inside its own transaction,
    ///     and the freeze — digest included — has to exist before that transaction can be handed its FreezeJson. The
    ///     freeze id is recorded in FreezeJson, so a run still names its copy unambiguously.
    /// </summary>
    public string FrozenDatasetPath(Guid datasetId, Guid freezeId) =>
        Path.Combine(TrainingRoot, "datasets", datasetId.ToString(), "frozen", $"{freezeId}.jsonl.enc");

    /// <summary>The decrypted training file the trainer opens.</summary>
    public string WorkDatasetPath(Guid runId) =>
        Path.Combine(WorkDirectory(runId), "dataset.jsonl");

    public string JobConfigPath(Guid runId) =>
        Path.Combine(WorkDirectory(runId), "job.json");

    /// <summary>Encrypts and writes the frozen copy through a temp file, so a crash never leaves a half-written freeze.</summary>
    public async Task WriteFrozenDatasetAsync(Guid datasetId, Guid freezeId, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
    {
        var path = FrozenDatasetPath(datasetId, freezeId);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encrypted = _protector.Encrypt(Guid.Empty, freezeId, FrozenDatasetColumn, plaintext.Span);
        var tempPath = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            await File.WriteAllBytesAsync(tempPath, encrypted, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            DeleteFileIfExists(tempPath);
            throw;
        }
    }

    /// <summary>Decrypts the frozen copy into the run's owner-only work directory and returns the path written.</summary>
    public async Task<string> MaterializeWorkCopyAsync(Guid datasetId, Guid freezeId, Guid runId, CancellationToken cancellationToken)
    {
        var frozen = await File.ReadAllBytesAsync(FrozenDatasetPath(datasetId, freezeId), cancellationToken).ConfigureAwait(false);
        var plaintext = _protector.Decrypt(Guid.Empty, freezeId, FrozenDatasetColumn, frozen);
        var workDirectory = WorkDirectory(runId);
        CreateOwnerOnlyDirectory(workDirectory);
        var target = WorkDatasetPath(runId);
        await File.WriteAllBytesAsync(target, plaintext, cancellationToken).ConfigureAwait(false);
        ApplyOwnerOnly(target);
        return target;
    }

    public void DeleteFrozenDataset(Guid datasetId, Guid freezeId) =>
        DeleteFileIfExists(FrozenDatasetPath(datasetId, freezeId));

    /// <summary>Removes the decrypted scratch. Best-effort: a failure here must never mask the run's own outcome.</summary>
    public void DeleteWorkDirectory(Guid runId)
    {
        var path = WorkDirectory(runId);
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The startup sweep collects whatever a live delete could not.
        }
    }

    public static void CreateOwnerOnlyDirectory(string path)
    {
        _ = Directory.CreateDirectory(path);
        ApplyOwnerOnly(path, directory: true);
    }

    private static void ApplyOwnerOnly(string path, bool directory = false)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path,
                directory
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // A filesystem that cannot express the mode (a mounted share) is not a reason to abandon the run.
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }
}
