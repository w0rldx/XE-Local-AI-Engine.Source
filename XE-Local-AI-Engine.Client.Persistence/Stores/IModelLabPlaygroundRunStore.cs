namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>A saved training-stepper run as the application layer sees it. <see cref="ResultJson" /> is already decrypted.</summary>
public sealed class ModelLabPlaygroundRunRecord
{
    public required Guid Id { get; init; }

    public required string TemplateId { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required Guid TraceBlobId { get; init; }

    public required string TraceSha256 { get; init; }

    public required long TraceByteCount { get; init; }

    public required ReadOnlyMemory<byte> ResultJson { get; init; }
}

/// <summary>A run to save, once its trace blob is written.</summary>
public sealed class ModelLabPlaygroundRunCreate
{
    public required Guid Id { get; init; }

    public required string TemplateId { get; init; }

    public required Guid TraceBlobId { get; init; }

    public required string TraceSha256 { get; init; }

    public required long TraceByteCount { get; init; }

    public required ReadOnlyMemory<byte> ResultJson { get; init; }
}

public interface IModelLabPlaygroundRunStore
{
    /// <summary>Saves the row and returns it as stored.</summary>
    Task<ModelLabPlaygroundRunRecord> CreateAsync(ModelLabPlaygroundRunCreate command, CancellationToken cancellationToken = default);

    Task<ModelLabPlaygroundRunRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<ModelLabPlaygroundRunRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>True when a row was removed.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Deletes the rows beyond the newest <paramref name="keep" /> and returns the ids actually removed.</summary>
    Task<IReadOnlyList<Guid>> TrimToNewestAsync(int keep, CancellationToken cancellationToken = default);
}
