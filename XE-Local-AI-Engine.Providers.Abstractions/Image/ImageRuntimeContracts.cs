namespace XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     A single text-to-image generation request handed to <see cref="IImageRuntime" />.
/// </summary>
/// <remarks>
///     Provider-neutral: it carries only the generation parameters, never any stable-diffusion.cpp flag, route, or HTTP
///     detail (those stay inside the adapter). The job/coordinator layer builds one of these per job and passes it to
///     <see cref="IImageRuntime.GenerateAsync" />.
/// </remarks>
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

    public int Width { get; init; } = 512;

    public int Height { get; init; } = 512;

    public int Steps { get; init; } = 20;

    /// <summary>Sampling method name (for example <c>euler_a</c>); <see langword="null" /> uses the runtime default.</summary>
    public string? Sampler { get; init; }

    public double CfgScale { get; init; } = 7.0;

    /// <summary>How many images to generate in the batch; the current runtime supports single-image jobs.</summary>
    public int BatchCount { get; init; } = 1;
}

/// <summary>The lifecycle phase of an image job.</summary>
/// <remarks>
///     The coarse values (<see cref="Queued" />, <see cref="Generating" /> and the three terminal ones) come from the runtime's HTTP job status,
///     all sd-server exposes; the four <em>fine</em> values (<see cref="Loading" />, <see cref="Encoding" />, <see cref="Sampling" />,
///     <see cref="Decoding" />) are observed out-of-band from the daemon's stdout progress lines, so a runtime that cannot read them never
///     reports them and the coarse transitions still stand. Only <see cref="Sampling" /> carries a step count and an estimate: <see cref="Loading" /> and
///     <see cref="Encoding" /> precede step 1 and <see cref="Decoding" /> — a large share of a small image's wall clock — follows the last step, all three deliberately countdown-free.
/// </remarks>
public enum ImageGenPhase
{
    /// <summary>Accepted and waiting for a generation slot.</summary>
    Queued = 0,

    /// <summary>Actively generating (not interruptible over HTTP — cancellation tree-kills + restarts the daemon).</summary>
    Generating = 1,

    Completed = 2,

    /// <summary>Failed; a sanitized error is surfaced.</summary>
    Failed = 3,

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
///     its phases.
/// </summary>
/// <remarks>
///     Every field except <see cref="Phase" /> and <see cref="Elapsed" /> is nullable, so a runtime that can only
///     observe the coarse HTTP status reports exactly what it knows and nothing more — an absent field means "not
///     observed", never "zero".
/// </remarks>
public sealed record ImageGenProgress
{
    public required ImageGenPhase Phase { get; init; }

    /// <summary>Queue position while <see cref="ImageGenPhase.Queued" /> (1-based), when the runtime reports one; otherwise <see langword="null" />.</summary>
    public int? QueuePosition { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>Completed sampling steps, when observed. Only ever set while <see cref="Phase" /> is <see cref="ImageGenPhase.Sampling" />.</summary>
    public int? Step { get; init; }

    /// <summary>Total sampling steps for this generation, when observed. Pairs with <see cref="Step" />.</summary>
    public int? TotalSteps { get; init; }

    /// <summary>Measured seconds per sampling iteration, when observed. The basis for <see cref="EstimatedRemaining" />.</summary>
    public double? SecondsPerIteration { get; init; }

    /// <summary>
    ///     Estimated time left in the SAMPLING phase only, when it can honestly be computed.
    /// </summary>
    /// <remarks>
    ///     Deliberately <see langword="null" /> outside <see cref="ImageGenPhase.Sampling" /> and once the last step is
    ///     done: the decode that follows has no observable progress, so a countdown there would sit at zero while the
    ///     job runs on.
    /// </remarks>
    public TimeSpan? EstimatedRemaining { get; init; }
}

/// <summary>
///     A completed image generation: the decoded PNG bytes plus the resolved metadata.
/// </summary>
/// <remarks>
///     The bytes are plaintext in memory only — the caller persists them through the encrypted-at-rest blob store.
/// </remarks>
public sealed record ImageGenerationResult
{
    /// <summary>The decoded image bytes (PNG). The caller persists these through the encrypted-at-rest blob store.</summary>
    public required ReadOnlyMemory<byte> ImageBytes { get; init; }

    /// <summary>
    ///     Width in pixels of the image that was actually produced — read from the returned payload, NOT echoed from the
    ///     request.
    /// </summary>
    /// <remarks>
    ///     Runtimes round the requested size (stable-diffusion.cpp snaps up to a multiple of 64), so this can
    ///     legitimately differ from <see cref="ImageGenerationRequest.Width" />.
    /// </remarks>
    public required int Width { get; init; }

    /// <summary>Height in pixels of the image that was actually produced; see <see cref="Width" />.</summary>
    public required int Height { get; init; }

    /// <summary>The seed actually used (the server-resolved value when the request asked for a random seed).</summary>
    public required long Seed { get; init; }

    /// <summary>The image format; currently always <c>png</c>.</summary>
    public string Format { get; init; } = "png";

    public TimeSpan Duration { get; init; }
}
