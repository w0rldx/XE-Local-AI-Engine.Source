namespace XE_Local_AI_Engine.Client.Services.Invocation.Resilience;

using System.ClientModel;
using System.Globalization;

/// <summary>Reads a provider's <c>Retry-After</c> header off a 429/503 SDK status failure as a typed wait.</summary>
internal static class ProviderRetryAfter
{
    /// <summary>
    ///     The wait the first 429 or 503 <see cref="ClientResultException" /> in the chain asks for, or null when there is
    ///     none, it carries no header, or the header is neither delta-seconds nor an IMF-fixdate HTTP-date.
    /// </summary>
    public static TimeSpan? Find(Exception exception, DateTimeOffset now)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ClientResultException { Status: 429 or 503 } statusFailure)
            {
                string? value = null;
                _ = statusFailure.GetRawResponse()?.Headers.TryGetValue("Retry-After", out value);
                return Parse(value, now);
            }
        }

        return null;
    }

    /// <summary>Delta-seconds, or an HTTP-date relative to <paramref name="now" />; a date in the past yields zero.</summary>
    public static TimeSpan? Parse(string? value, DateTimeOffset now)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(Math.Min(seconds, int.MaxValue));
        }

        if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var wait = date - now;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }
}
