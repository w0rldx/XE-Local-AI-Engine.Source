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
    ///     Generates an image for <paramref name="request" />, pushing coarse phase transitions
    ///     (queued → generating → completed/failed) to <paramref name="progress" />, and returns the decoded result.
    /// </summary>
    /// <param name="request">The generation parameters (prompt, size, steps, sampler, seed, model).</param>
    /// <param name="progress">Receives coarse <see cref="ImageGenProgress" /> transitions; NO step/percent detail.</param>
    /// <param name="ct">
    ///     Cancels the generation. A still-queued job is cancelled cleanly over HTTP; a job already generating is aborted
    ///     by tree-killing and restarting the runtime daemon (sd-server cannot interrupt an in-flight generation).
    /// </param>
    /// <exception cref="OperationCanceledException"><paramref name="ct" /> was signalled.</exception>
    Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, IProgress<ImageGenProgress> progress, CancellationToken ct);
}
