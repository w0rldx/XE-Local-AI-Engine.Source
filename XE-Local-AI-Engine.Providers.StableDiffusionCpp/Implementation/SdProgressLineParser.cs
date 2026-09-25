namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Turns one drained <c>sd-server</c> stdout line into a <see cref="SdProgressObservation" />, or into nothing, and
///     is the ONLY place sd.cpp's console output format is interpreted.
/// </summary>
/// <remarks>
///     The rate token is the anchor, NOT the fraction: three different sd.cpp lines carry an <c>N/M</c> pair and only the one ending in
///     a per-iteration rate is a sampler step. Captured by hexdump at <c>master-742-1a13107</c> (the capture behind
///     <see cref="SdOutputFrameSplitter" />); the printer and anchor lines are source-identical at <c>master-913-b167b94</c>. See
///     docs/wiki/14-image-generation.md ("The rate token is the anchor, not the fraction").
/// </remarks>
internal static partial class SdProgressLineParser
{
    private const int RegexTimeoutMilliseconds = 1000;

    /// <summary>Below one iteration per second sd.cpp prints <c>s/it</c>; above it, <c>it/s</c>. Both normalize to seconds.</summary>
    private const string SecondsPerIterationUnit = "s/it";

    /// <summary>Parses <paramref name="line" />, returning <see langword="false" /> for the vast majority that carry no progress.</summary>
    public static bool TryParse(string? line, [NotNullWhen(true)] out SdProgressObservation? observation)
    {
        observation = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var samplerStep = SamplerStepPattern().Match(line);
        if (samplerStep.Success)
        {
            observation = BuildSamplingObservation(samplerStep);
            return observation is not null;
        }

        // Ordered by position in a generation, but the consumer does NOT rely on that order: the daemon loads the VAE weights AFTER sampling, so "loading" legitimately reappears late. Ordering is
        // resolved by the consumer's sampling-seen latch, not here.
        if (DecodingPattern().IsMatch(line))
        {
            observation = new SdProgressObservation
            {
                Phase = ImageGenPhase.Decoding
            };
            return true;
        }

        if (EncodingPattern().IsMatch(line))
        {
            observation = new SdProgressObservation
            {
                Phase = ImageGenPhase.Encoding
            };
            return true;
        }

        if (LoadingPattern().IsMatch(line) || ByteRateBarPattern().IsMatch(line))
        {
            observation = new SdProgressObservation
            {
                Phase = ImageGenPhase.Loading
            };
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Builds the sampling observation from a matched step line, normalizing the rate to seconds per iteration.
    /// </summary>
    /// <remarks>
    ///     Returns <see langword="null" /> for a nonsensical counter (step 0, total 0, step past total) so a garbled
    ///     line degrades to "no observation" instead of a bar that runs backwards or past its end.
    /// </remarks>
    private static SdProgressObservation? BuildSamplingObservation(Match match)
    {
        if (!int.TryParse(match.Groups["step"].ValueSpan, CultureInfo.InvariantCulture, out var step)
            || !int.TryParse(match.Groups["total"].ValueSpan, CultureInfo.InvariantCulture, out var total)
            || !double.TryParse(match.Groups["rate"].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
        {
            return null;
        }

        if (step <= 0 || total <= 0 || step > total)
        {
            return null;
        }

        return new SdProgressObservation
        {
            Phase = ImageGenPhase.Sampling,
            Step = step,
            TotalSteps = total,
            SecondsPerIteration = ToSecondsPerIteration(rate, match.Groups["unit"].Value)
        };
    }

    /// <summary>
    ///     Normalizes the printed rate to seconds per iteration. sd.cpp switches units at one iteration per second, so
    ///     without this the consumer would see two different scales for the same quantity mid-generation.
    /// </summary>
    private static double? ToSecondsPerIteration(double rate, string unit)
    {
        if (rate <= 0)
        {
            return null;
        }

        return unit.Equals(SecondsPerIterationUnit, StringComparison.Ordinal) ? rate : 1.0 / rate;
    }

    /// <summary>
    ///     The sampler step bar. Anchored at end-of-line on the iteration-rate token, which is what separates it from
    ///     the tensor-loader bar (<c>MB/s</c>) and the batch counter (no rate at all).
    /// </summary>
    [GeneratedRegex(@"(?<step>\d+)\s*/\s*(?<total>\d+)\s*-\s*(?<rate>\d+(?:\.\d+)?)\s*(?<unit>s/it|it/s)\s*$",
        RegexOptions.ExplicitCapture,
        RegexTimeoutMilliseconds)]
    private static partial Regex SamplerStepPattern();

    /// <summary>The tensor-loader bar, matched only to keep the phase on "loading" while weights stream in.</summary>
    [GeneratedRegex(@"\d+\s*/\s*\d+\s*-\s*\d+(?:\.\d+)?\s*(?:[KMGT]?B)/s\s*$", RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex ByteRateBarPattern();

    /// <summary>Weight loading, either the daemon's initial model open or a lazily prepared component's tensors.</summary>
    [GeneratedRegex(@"loading (?:model from |\d+/\d+ tensors)", RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex LoadingPattern();

    /// <summary>
    ///     Prompt conditioning, which runs entirely before step 1. <c>get_learned_condition completed</c> marks its end;
    ///     the sampler announcement and the per-image banner bracket the same pre-step window.
    /// </summary>
    [GeneratedRegex(@"get_learned_condition completed|generate_image \d+x\d+|sampling using ", RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex EncodingPattern();

    /// <summary>
    ///     VAE decode, which runs after the last sampling step and has no step counter of its own. This is the phase a
    ///     step-only ETA silently sits through at "0s remaining".
    /// </summary>
    [GeneratedRegex(@"sampling completed|decoding \d+ latents|decode_first_stage completed", RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex DecodingPattern();
}
