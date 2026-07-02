namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Persistence boundary for the local image-generation job registry (<c>image_jobs</c>). The prompt / negative-prompt
///     columns are encrypted at rest by the node encryption interceptors, so every write goes through EF
///     <see cref="Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(System.Threading.CancellationToken)" /> (never
///     raw SQL) — the interceptor encrypts the prompt on insert and skips re-encrypting it on a status-only update
///     (the property is unmodified), keeping the ciphertext intact. Reads decrypt via the materialization interceptor.
///     Consumed by the singleton <c>ImageJobCoordinator</c> through a fresh DI scope per operation.
/// </summary>
public interface IImageJobStore
{
    /// <summary>Inserts a new job in the <see cref="ImageJobStatus.Queued" /> state (encrypting the prompt at rest).</summary>
    Task CreateQueuedAsync(ImageJobCreate create, CancellationToken cancellationToken);

    /// <summary>Reads one job's decrypted view, or <see langword="null" /> when it does not exist.</summary>
    Task<ImageJobView?> GetAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Lists every job (newest first) as a decrypted view.</summary>
    Task<IReadOnlyList<ImageJobView>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Transitions a job to <see cref="ImageJobStatus.Generating" /> and records its start time.</summary>
    Task MarkGeneratingAsync(Guid jobId, long startedAtUtc, CancellationToken cancellationToken);

    /// <summary>Marks a job <see cref="ImageJobStatus.Succeeded" />, recording the produced image id, completion time, and duration.</summary>
    Task MarkSucceededAsync(Guid jobId, Guid imageId, long completedAtUtc, long durationMs, CancellationToken cancellationToken);

    /// <summary>Marks a job <see cref="ImageJobStatus.Failed" /> with a display-safe (already sanitized) error.</summary>
    Task MarkFailedAsync(Guid jobId, string sanitizedError, long completedAtUtc, CancellationToken cancellationToken);

    /// <summary>Marks a job <see cref="ImageJobStatus.Cancelled" /> and records its completion time.</summary>
    Task MarkCancelledAsync(Guid jobId, long completedAtUtc, CancellationToken cancellationToken);

    /// <summary>Records that a cancel was requested for a job (does not itself transition the status).</summary>
    Task MarkCancellationRequestedAsync(Guid jobId, long requestedAtUtc, CancellationToken cancellationToken);
}

/// <summary>
///     The parameters for a new queued image job. <see cref="Prompt" /> / <see cref="NegativePrompt" /> are plaintext
///     here; the store encodes them to UTF-8 bytes and the node encryption interceptor encrypts them at rest.
/// </summary>
public sealed record ImageJobCreate
{
    public required Guid Id { get; init; }
    public required string ModelName { get; init; }
    public required string Prompt { get; init; }
    public string? NegativePrompt { get; init; }
    public long Seed { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Steps { get; init; }
    public required string Sampler { get; init; }
    public double CfgScale { get; init; }
    public long CreatedAtUtc { get; init; }
}

/// <summary>
///     A decrypted, transport-neutral view of a persisted image job. <see cref="Prompt" /> / <see cref="NegativePrompt" />
///     are the decrypted plaintext (returned only to the authenticated operator; never logged).
/// </summary>
public sealed record ImageJobView
{
    public required Guid Id { get; init; }
    public required string ModelName { get; init; }
    public required string Prompt { get; init; }
    public string? NegativePrompt { get; init; }
    public required long Seed { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Steps { get; init; }
    public required string Sampler { get; init; }
    public required double CfgScale { get; init; }
    public required ImageJobStatus Status { get; init; }
    public required long CreatedAtUtc { get; init; }
    public long? StartedAtUtc { get; init; }
    public long? CompletedAtUtc { get; init; }
    public long? DurationMs { get; init; }
    public Guid? ImageId { get; init; }
    public string? SanitizedError { get; init; }
    public long? CancellationRequestedAtUtc { get; init; }
}
