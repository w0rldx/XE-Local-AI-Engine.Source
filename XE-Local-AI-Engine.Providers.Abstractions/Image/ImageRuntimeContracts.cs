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
///     The coarse lifecycle phase of an image job. Deliberately step-free: the sd-server HTTP contract exposes NO
///     step/percent/preview progress, so progress is a coarse status transition, never a step bar.
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
    Cancelled = 4
}

/// <summary>
///     One coarse progress observation pushed to the caller-supplied <see cref="IProgress{T}" /> as an image job moves
///     through its phases. NO step or percent field — the runtime cannot observe sub-generation progress over HTTP.
/// </summary>
public sealed record ImageGenProgress
{
    /// <summary>The coarse phase this observation reports.</summary>
    public required ImageGenPhase Phase { get; init; }

    /// <summary>Queue position while <see cref="ImageGenPhase.Queued" /> (1-based), when the runtime reports one; otherwise <see langword="null" />.</summary>
    public int? QueuePosition { get; init; }

    /// <summary>Wall-clock elapsed time since generation started.</summary>
    public TimeSpan Elapsed { get; init; }
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
