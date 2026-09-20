namespace XE_Local_AI_Engine.Client.Services.LlamaCpp;

using System.Globalization;

/// <summary>Shared comparison logic for llama.cpp runtime release tags.</summary>
/// <remarks>
///     A tag is the validated form <c>b&lt;number&gt;</c> (regex <c>^b\d+$</c>, enforced at the transport boundary by <c>StoredNodeSettings.IsValidRecommendedLlamaCppTag</c>
///     and <c>NodeSettingsEndpointValidators</c>). Because the numeric suffix is monotonically increasing upstream, an update is only
///     "available" when the installed build is strictly OLDER than the recommended one — a string inequality would falsely advertise a
///     downgrade as an update when the installed tag is newer than the recommended one (e.g. installed <c>b9700</c> vs recommended <c>b9692</c>).
/// </remarks>
public static class LlamaCppRuntimeTag
{
    /// <summary>Returns <see langword="true" /> when a newer llama.cpp runtime than the installed one is recommended.</summary>
    /// <remarks>
    ///     No recommended tag (null or empty) reads as nothing to recommend, <see langword="false" />; no installed tag reads as a fresh node and offers
    ///     the recommended build as an install, <see langword="true" />. When both parse as <c>b&lt;number&gt;</c> the answer is
    ///     <c>installed &lt; recommended</c>, so a downgrade is never advertised. When either is an unexpected non-<c>b&lt;number&gt;</c> value it falls
    ///     back to <c>!string.Equals(installed, recommended, Ordinal)</c>, which can never throw on the hot GET path and keeps the "differs ⇒ update"
    ///     semantics for malformed tags rather than risking an exception or silently hiding an update.
    /// </remarks>
    public static bool IsUpdateAvailable(string? installedTag, string? recommendedTag)
    {
        if (string.IsNullOrWhiteSpace(recommendedTag))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(installedTag))
        {
            return true;
        }

        var installedNumber = TryParseTagNumber(installedTag);
        var recommendedNumber = TryParseTagNumber(recommendedTag);

        if (installedNumber is { } installed && recommendedNumber is { } recommended)
        {
            return installed < recommended;
        }

        // Unexpected non-b<number> tag(s): preserve the original string-inequality behavior so we never throw here.
        return !string.Equals(installedTag, recommendedTag, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Parses the integer after the leading <c>b</c> of a llama.cpp release tag, or <see langword="null" /> when
    ///     <paramref name="tag" /> is not in the <c>b&lt;number&gt;</c> form.
    /// </summary>
    public static long? TryParseTagNumber(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || (tag[0] != 'b' && tag[0] != 'B'))
        {
            return null;
        }

        return long.TryParse(tag.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }
}
