namespace XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     A single text-to-image generation request handed to <see cref="IImageRuntime" />. Provider-neutral: it carries
///     only the generation parameters, never any stable-diffusion.cpp flag, route, or HTTP detail (those stay inside the
///     adapter). The job/coordinator layer builds one of these per job and passes it to
///     <see cref="IImageRuntime.GenerateAsync" />.
/// </summary>
public sealed record ImageGenerationRequest
{
    /// <summary>Registry key of the installed image model to generate with.</summary>
    public required string ModelName { get; init; }

    /// <summary>The positive prompt describing the desired image. Never logged (privacy — redacted at every boundary).</summary>
    public required string Prompt { get; init; }

    /// <summary>Optional negative prompt (concepts to steer away from). Never logged.</summary>
    public string? NegativePrompt { get; init; }

    /// <summary>Random seed; <c>-1</c> requests a server-chosen random seed (the actual seed is returned in the result).</summary>
    public long Seed { get; init; } = -1;

    /// <summary>Output width in pixels.</summary>
    public int Width { get; init; } = 512;

    /// <summary>Output height in pixels.</summary>
    public int Height { get; init; } = 512;

    /// <summary>Number of diffusion steps.</summary>
    public int Steps { get; init; } = 20;

    /// <summary>Sampling method name (for example <c>euler_a</c>); <see langword="null" /> uses the runtime default.</summary>
    public string? Sampler { get; init; }

    /// <summary>Classifier-free-guidance scale.</summary>
    public double CfgScale { get; init; } = 7.0;

    /// <summary>How many images to generate in the batch. Step 1 ships single-image jobs.</summary>
    public int BatchCount { get; init; } = 1;
}

/// <summary>
///     The lifecycle phase of an image job.
///     <para>
///         The coarse values (<see cref="Queued" />, <see cref="Generating" /> and the three terminal ones) come from the
///         runtime's HTTP job status, which is all sd-server's HTTP contract exposes. The four <em>fine</em> values
///         (<see cref="Loading" />, <see cref="Encoding" />, <see cref="Sampling" />, <see cref="Decoding" />) are
///         observed out-of-band from the daemon's own stdout progress lines, so a runtime that cannot read them simply
///         never reports them and the coarse transitions still stand on their own.
///     </para>
///     <para>
///         The fine values are load-bearing for an honest countdown: a step-only ETA reaches zero at the last sampling
///         step and then sits there through VAE decode, which on a small image is a large share of the wall clock. Only
///         <see cref="Sampling" /> carries a step count and an estimate; <see cref="Loading" />/<see cref="Encoding" />
///         precede step 1 and <see cref="Decoding" /> follows the last step, and all three are deliberately
///         countdown-free.
///     </para>
/// </summary>
public enum ImageGenPhase
{
    /// <summary>Accepted and waiting for a generation slot.</summary>
    Queued = 0,

    /// <summary>Actively generating (not interruptible over HTTP — cancellation tree-kills + restarts the daemon).</summary>
    Generating = 1,

    /// <summary>Finished successfully; the decoded image is available.</summary>
    Completed = 2,

    /// <summary>Failed; a sanitized error is surfaced.</summary>
    Failed = 3,

    /// <summary>Cancelled at the caller's request.</summary>
    Cancelled = 4,

    /// <summary>Fine phase: the runtime is reading model weights for this generation. No step count, no countdown.</summary>
    Loading = 5,

    /// <summary>Fine phase: the prompt is being encoded (runs entirely BEFORE step 1). No step count, no countdown.</summary>
    Encoding = 6,

    /// <summary>Fine phase: the diffusion sampler is running. The only phase that carries a step count and an estimate.</summary>
    Sampling = 7,

    /// <summary>Fine phase: the latent is being decoded to pixels (runs AFTER the last step). No step count, no countdown.</summary>
    Decoding = 8
}

/// <summary>
///     One progress observation pushed to the caller-supplied <see cref="IProgress{T}" /> as an image job moves through
///     its phases. Every field except <see cref="Phase" /> and <see cref="Elapsed" /> is nullable, so a runtime that can
///     only observe the coarse HTTP status reports exactly what it knows and nothing more — an absent field means
///     "not observed", never "zero".
/// </summary>
public sealed record ImageGenProgress
{
    /// <summary>The phase this observation reports.</summary>
    public required ImageGenPhase Phase { get; init; }

    /// <summary>Queue position while <see cref="ImageGenPhase.Queued" /> (1-based), when the runtime reports one; otherwise <see langword="null" />.</summary>
    public int? QueuePosition { get; init; }

    /// <summary>Wall-clock elapsed time since generation started.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Completed sampling steps, when observed. Only ever set while <see cref="Phase" /> is <see cref="ImageGenPhase.Sampling" />.</summary>
    public int? Step { get; init; }

    /// <summary>Total sampling steps for this generation, when observed. Pairs with <see cref="Step" />.</summary>
    public int? TotalSteps { get; init; }

    /// <summary>Measured seconds per sampling iteration, when observed. The basis for <see cref="EstimatedRemaining" />.</summary>
    public double? SecondsPerIteration { get; init; }

    /// <summary>
    ///     Estimated time left in the SAMPLING phase only, when it can honestly be computed. Deliberately
    ///     <see langword="null" /> outside <see cref="ImageGenPhase.Sampling" /> and once the last step is done: the
    ///     decode that follows has no observable progress, so a countdown there would sit at zero while the job runs on.
    /// </summary>
    public TimeSpan? EstimatedRemaining { get; init; }
}

/// <summary>
///     A completed image generation: the decoded PNG bytes plus the resolved metadata. The bytes are plaintext in
///     memory only — the caller persists them through the encrypted-at-rest blob store.
/// </summary>
public sealed record ImageGenerationResult
{
    /// <summary>The decoded image bytes (PNG). The caller persists these through the encrypted-at-rest blob store.</summary>
    public required ReadOnlyMemory<byte> ImageBytes { get; init; }

    /// <summary>
    ///     Width in pixels of the image that was actually produced — read from the returned payload, NOT echoed from the
    ///     request. Runtimes round the requested size (stable-diffusion.cpp snaps up to a multiple of 64), so this can
    ///     legitimately differ from <see cref="ImageGenerationRequest.Width" />.
    /// </summary>
    public required int Width { get; init; }

    /// <summary>Height in pixels of the image that was actually produced; see <see cref="Width" />.</summary>
    public required int Height { get; init; }

    /// <summary>The seed actually used (the server-resolved value when the request asked for a random seed).</summary>
    public required long Seed { get; init; }

    /// <summary>The image format (always <c>png</c> in step 1).</summary>
    public string Format { get; init; } = "png";

    /// <summary>Total wall-clock time the generation took.</summary>
    public TimeSpan Duration { get; init; }
}
