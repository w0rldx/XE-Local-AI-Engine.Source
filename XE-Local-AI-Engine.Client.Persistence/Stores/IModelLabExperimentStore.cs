namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>A Model Lab experiment as the application layer sees it. Encrypted columns are already decrypted.</summary>
public sealed class ModelLabExperimentRecord
{
    public required Guid Id { get; init; }

    public required ModelLabExperimentStatus Status { get; init; }

    public required ModelLabExperimentKind Kind { get; init; }

    public required string RepoId { get; init; }

    public required string Revision { get; init; }

    public required Guid BaseArtifactId { get; init; }

    public required string InstalledModelName { get; init; }

    public required string Prompt { get; init; }

    public required ReadOnlyMemory<byte>? ResultJson { get; init; }

    public required string? ErrorMessage { get; init; }

    public required Guid? TraceBlobId { get; init; }

    public required string? TraceSha256 { get; init; }

    public required long? TraceByteCount { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed class ModelLabExperimentCreate
{
    public required Guid Id { get; init; }

    public ModelLabExperimentKind Kind { get; init; } = ModelLabExperimentKind.Trace;

    public required string RepoId { get; init; }

    public required string Revision { get; init; }

    public required Guid BaseArtifactId { get; init; }

    public required string InstalledModelName { get; init; }

    public required string Prompt { get; init; }

    public required ReadOnlyMemory<byte> LicenseConfirmationJson { get; init; }
}

/// <summary>How a job ended. The blob fields are set together, and only when its blob has been written. What is left null keeps what the row already holds.</summary>
public sealed record ModelLabExperimentCompletion
{
    public required ModelLabExperimentStatus Status { get; init; }

    public string? ErrorMessage { get; init; }

    public ReadOnlyMemory<byte>? ResultJson { get; init; }

    public Guid? TraceBlobId { get; init; }

    public string? TraceSha256 { get; init; }

    public long? TraceByteCount { get; init; }
}

/// <summary>A blob written to the Model Lab store, as a row refers to it.</summary>
public sealed class ModelLabBlobReference
{
    public required Guid BlobId { get; init; }

    public required string Sha256 { get; init; }

    public required long ByteCount { get; init; }
}

public enum ModelLabExperimentDeleteOutcome
{
    Deleted,
    NotFound,

    /// <summary>Its job is live. The service decides this: a row that merely says <c>Running</c> may have no job.</summary>
    Running,

    /// <summary>It still holds the receipt the next startup needs to find and kill an orphan.</summary>
    ProcessNotConfirmedStopped
}

/// <summary>One recorded process, for the startup reaper.</summary>
public sealed class ModelLabLaunchReceipt
{
    public required Guid ExperimentId { get; init; }

    public required ReadOnlyMemory<byte> LaunchReceiptJson { get; init; }
}

public interface IModelLabExperimentStore
{
    Task CreateRunningAsync(ModelLabExperimentCreate command, CancellationToken cancellationToken = default);

    Task<ModelLabExperimentRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<ModelLabExperimentRecord>> ListAsync(CancellationToken cancellationToken = default);

    Task SetLaunchReceiptAsync(Guid id, ReadOnlyMemory<byte>? launchReceiptJson, CancellationToken cancellationToken = default);

    /// <summary>Saves what a running job has so far, so a job that fails or dies later still shows it. A null keeps what the row holds.</summary>
    Task SetProgressAsync(Guid id, ReadOnlyMemory<byte> resultJson, ModelLabBlobReference? blob, CancellationToken cancellationToken = default);

    /// <summary>Terminalizes a <c>Running</c> row and clears its receipt: the caller has seen the process exit.</summary>
    Task CompleteAsync(Guid id, ModelLabExperimentCompletion completion, CancellationToken cancellationToken = default);

    Task<ModelLabExperimentDeleteOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Deletes finished rows beyond the newest <paramref name="keep" /> and returns their ids. A <c>Running</c> row or one holding a receipt stays.</summary>
    Task<IReadOnlyList<Guid>> TrimToNewestAsync(int keep, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModelLabLaunchReceipt>> ListLaunchReceiptsAsync(CancellationToken cancellationToken = default);

    /// <summary>Fails every <c>Running</c> row. Leaves receipts alone: only the reaper clears one, once it has ruled out the process.</summary>
    Task<IReadOnlyList<Guid>> RecoverOnStartupAsync(CancellationToken cancellationToken = default);
}
