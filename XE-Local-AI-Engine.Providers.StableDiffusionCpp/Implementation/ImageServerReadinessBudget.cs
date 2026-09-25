namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;

/// <summary>
///     Sizes the readiness budget for one <c>sd-server</c> spawn against the file-set it has to load.
/// </summary>
/// <remarks>
///     sd-server binds its listening socket only <em>after</em> the synchronous model load finishes, so the readiness wait is really a
///     model-load wait, and a flat budget encodes an assumption about model size: the two minutes that comfortably covers a ~2 GB SD1.5
///     file is not enough for the ~10 GB Qwen-Image 2.1 set (4.2 GB DiT + 0.7 GB VAE + 5.0 GB Qwen3-VL-8B encoder pinned to
///     CPU), and the operator then sees "did not become ready in time" — a message that blames the model for a budget that was too small.
/// </remarks>
internal static class ImageServerReadinessBudget
{
    /// <summary>
    ///     The readiness budget for <paramref name="parts" />: the configured floor, or the size-scaled estimate when
    ///     that is larger, capped by <see cref="StableDiffusionRuntimeOptions.MaxReadinessTimeout" />.
    /// </summary>
    /// <remarks>
    ///     Parts reporting a non-positive size contribute nothing, so a registry without sizes degrades to the flat
    ///     floor rather than to zero.
    /// </remarks>
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
