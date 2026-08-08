namespace XE_Local_AI_Engine.Client.Models;

using System.Globalization;

/// <summary>
///     Shared parse/validation for the RNG seed fields carried on the wire as strings (chat
///     <see cref="SamplingOptions.Seed" /> and the image job request/response seed). A seed is an unconstrained public
///     64-bit value, so serializing it as a JSON number loses precision above <c>2^53</c>: a large seed the backend
///     accepts is silently rounded on the wire and then rejected by the client's safe-integer validator (Blocker 3). The
///     string form survives the wire exactly; this helper is the single place that recovers the <see cref="long" /> and
///     rejects a malformed value with one consistent message.
/// </summary>
public static class SeedValue
{
    /// <summary>The operator-safe rejection message for a seed string that is not a base-10 64-bit integer.</summary>
    public const string ValidationMessage =
        "Seed must be a whole number between -9223372036854775808 and 9223372036854775807.";

    /// <summary>
    ///     Parses an optional wire seed to a nullable <see cref="long" />. A null/blank value is a valid "no seed"
    ///     (<paramref name="seed" /> = <see langword="null" />). A non-blank value must be a base-10 64-bit integer;
    ///     otherwise <paramref name="error" /> is set to <see cref="ValidationMessage" /> and the method returns
    ///     <see langword="false" />.
    /// </summary>
    public static bool TryParse(string? raw, out long? seed, out string? error)
    {
        seed = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (long.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
        {
            seed = parsed;
            return true;
        }

        error = ValidationMessage;
        return false;
    }

    /// <summary>True when <paramref name="raw" /> is a valid optional seed (blank, or a base-10 64-bit integer).</summary>
    public static bool IsValid(string? raw) =>
        TryParse(raw, out _, out _);

    /// <summary>Formats a seed <see cref="long" /> for the wire.</summary>
    public static string ToWire(long seed) =>
        seed.ToString(CultureInfo.InvariantCulture);
}
