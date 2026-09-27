namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

using System.Globalization;

/// <summary>The llama-server <c>/metrics</c> series the node reads, and the reader for its Prometheus text.</summary>
public static class LlamaServerMetrics
{
    public const string PromptTokensTotal = "llamacpp:prompt_tokens_total";
    public const string TokensPredictedTotal = "llamacpp:tokens_predicted_total";
    public const string PromptSecondsTotal = "llamacpp:prompt_seconds_total";
    public const string TokensPredictedSecondsTotal = "llamacpp:tokens_predicted_seconds_total";
    public const string RequestsProcessing = "llamacpp:requests_processing";
    public const string RequestsDeferred = "llamacpp:requests_deferred";
    public const string ContextTokensHighWatermark = "llamacpp:n_tokens_max";
    public const string BusySlotsPerDecode = "llamacpp:n_busy_slots_per_decode";

    /// <summary>
    ///     Extracts the first sample of the Prometheus metric named <paramref name="name" /> from a <c>/metrics</c>
    ///     text scrape.
    /// </summary>
    /// <returns><see langword="null" /> when the metric is absent or unparseable.</returns>
    /// <remarks>
    ///     Pure and culture-invariant, so it is unit-testable without a live server. Tolerates label sets and trailing
    ///     timestamps; comment lines are skipped.
    /// </remarks>
    public static double? TryParse(string? text, string name)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var span = line.AsSpan().Trim();
            if (span.IsEmpty || span[0] == '#' || !span.StartsWith(name, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = span[name.Length..];
            if (rest.IsEmpty)
            {
                continue;
            }

            if (rest[0] == '{')
            {
                var close = rest.IndexOf('}');
                if (close < 0)
                {
                    continue;
                }

                rest = rest[(close + 1)..];
            }
            else if (rest[0] is not ' ' and not '\t')
            {
                continue;
            }

            rest = rest.Trim();
            var separator = rest.IndexOfAny(' ', '\t');
            var valueSpan = separator >= 0 ? rest[..separator] : rest;
            if (double.TryParse(valueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return null;
    }
}
