namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;

/// <summary>
///     Sizes the readiness budget for one <c>sd-server</c> spawn against the file-set it has to load.
///     <para>
///         sd-server binds its listening socket only <em>after</em> the synchronous model load finishes, so the readiness
///         wait is really a model-load wait. A flat budget therefore encodes an assumption about model size: the two
///         minutes that comfortably covers a ~2 GB SD1.5 file is not enough for an ~18 GB Qwen-Image set (diffusion
///         transformer + 7B LLM text encoder + VAE, with the encoder pinned to CPU), and the operator sees "did not
///         become ready in time" — a message that blames the model for a budget that was too small.
///     </para>
/// </summary>
internal static class ImageServerReadinessBudget
{
    /// <summary>
    ///     The readiness budget for <paramref name="parts" />: the configured floor, or the size-scaled estimate when
    ///     that is larger, capped by <see cref="StableDiffusionRuntimeOptions.MaxReadinessTimeout" />. Parts reporting a
    ///     non-positive size contribute nothing, so a registry without sizes degrades to the flat floor rather than to
    ///     zero.
    /// </summary>
    internal static TimeSpan For(IReadOnlyList<ImageModelPart> parts, StableDiffusionRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(options);

        var floor = options.ReadinessTimeout;
        if (options.ReadinessLoadBytesPerSecond <= 0)
        {
            return floor;
        }

        var totalBytes = parts.Where(static part => part.SizeBytes > 0).Sum(static part => part.SizeBytes);
        if (totalBytes <= 0)
        {
            return floor;
        }

        var scaled = TimeSpan.FromSeconds((double)totalBytes / options.ReadinessLoadBytesPerSecond);
        var budget = scaled > floor ? scaled : floor;
        return budget > options.MaxReadinessTimeout ? options.MaxReadinessTimeout : budget;
    }
}
