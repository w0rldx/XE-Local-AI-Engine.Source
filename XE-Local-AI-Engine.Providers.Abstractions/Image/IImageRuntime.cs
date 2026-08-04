namespace XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     The sole entry point to local image generation. The single implementation
///     (<c>StableDiffusionCppRuntime</c>) hides every stable-diffusion.cpp <c>sd-server</c> flag, route, port, and HTTP
///     shape behind this provider-neutral seam — no sd.cpp detail crosses this boundary.
///     Consumed by the job coordinator; one job → one <see cref="GenerateAsync" /> call.
/// </summary>
public interface IImageRuntime
{
    /// <summary>
    ///     Generates an image for <paramref name="request" />, pushing phase transitions
    ///     (queued → generating → completed/failed) to <paramref name="progress" />, and returns the decoded result.
    /// </summary>
    /// <param name="request">The generation parameters (prompt, size, steps, sampler, seed, model).</param>
    /// <param name="progress">
    ///     Receives <see cref="ImageGenProgress" /> transitions. An implementation that can observe the fine phases
    ///     (load / encode / sample / decode) also reports a sampling step count and an estimate; one that cannot reports
    ///     only the coarse transitions, leaving every optional field <see langword="null" />.
    /// </param>
    /// <param name="ct">
    ///     Cancels the generation. A still-queued job is cancelled cleanly over HTTP; a job already generating is aborted
    ///     by tree-killing and restarting the runtime daemon (sd-server cannot interrupt an in-flight generation).
    /// </param>
    /// <exception cref="OperationCanceledException"><paramref name="ct" /> was signalled.</exception>
    Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, IProgress<ImageGenProgress> progress, CancellationToken ct);
}
