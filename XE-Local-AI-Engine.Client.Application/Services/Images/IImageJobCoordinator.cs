namespace XE_Local_AI_Engine.Client.Services.Images;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     On-demand image-job orchestrator. Mirrors the GGUF download coordinator: an in-flight
///     <see cref="System.Threading.CancellationTokenSource" /> registry keyed by job id, coarse throttled progress push,
///     and detached run tasks. Generation is <b>serialized to at most one running job</b> (extra jobs stay
///     <see cref="ImageJobStatus.Queued" /> in this coordinator and are NOT handed to the runtime until the slot frees),
///     so a cancel that must tree-kill the daemon has a blast radius of exactly one job (§4A/§7.5). Singleton — the
///     registry must outlive the request that started a job. Job status is persisted to <c>image_jobs</c>; the produced
///     image is persisted encrypted-at-rest before the coordinator marks the job succeeded.
/// </summary>
public interface IImageJobCoordinator
{
    /// <summary>
    ///     Persists a new <see cref="ImageJobStatus.Queued" /> job, mints its cancellation token, kicks the serialized
    ///     worker, and returns the job id. The generation runs detached after this call returns.
    /// </summary>
    Task<Guid> EnqueueAsync(CreateImageJobInput input, CancellationToken cancellationToken);

    /// <summary>
    ///     Requests cancellation of a tracked job by signalling its token: a still-queued job is dropped to
    ///     <see cref="ImageJobStatus.Cancelled" /> without ever calling the runtime; a generating job's token is cancelled
    ///     (the runtime performs the queued-cancel or kill+restart). Returns <see langword="false" /> when the job is
    ///     unknown or already terminal.
    /// </summary>
    Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Reads one job's current status view, or <see langword="null" /> when unknown.</summary>
    Task<ImageJobView?> GetAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Lists every persisted job (newest first).</summary>
    Task<IReadOnlyList<ImageJobView>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Returns the job's buffered status events (in seq order) for a late hub subscriber to replay. Empty when the job
    ///     has no live replay log (already evicted or never seen).
    /// </summary>
    IReadOnlyList<ImageJobBufferedEvent> SnapshotBufferedEvents(Guid jobId);
}

/// <summary>
///     The parameters for a new image job as handed to the coordinator by the create endpoint (Lane D). Provider-neutral;
///     the coordinator maps it to the runtime request and the persisted job row.
/// </summary>
public sealed record CreateImageJobInput
{
    /// <summary>Registry key of the installed image model to generate with.</summary>
    public required string ModelName { get; init; }

    /// <summary>The positive prompt. Persisted encrypted at rest; never logged.</summary>
    public required string Prompt { get; init; }

    /// <summary>Optional negative prompt. Persisted encrypted at rest; never logged.</summary>
    public string? NegativePrompt { get; init; }

    /// <summary>Random seed; <c>-1</c> requests a runtime-chosen random seed.</summary>
    public long Seed { get; init; } = -1;

    /// <summary>Output width in pixels.</summary>
    public int Width { get; init; } = 512;

    /// <summary>Output height in pixels.</summary>
    public int Height { get; init; } = 512;

    /// <summary>Number of diffusion steps.</summary>
    public int Steps { get; init; } = 20;

    /// <summary>Sampling method name; <see langword="null" /> uses the runtime default.</summary>
    public string? Sampler { get; init; }

    /// <summary>Classifier-free-guidance scale.</summary>
    public double CfgScale { get; init; } = 7.0;
}
