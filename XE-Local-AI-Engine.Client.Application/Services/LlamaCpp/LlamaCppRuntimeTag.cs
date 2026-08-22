namespace XE_Local_AI_Engine.Client.Services.LlamaCpp;

using System.Globalization;

/// <summary>
///     Shared comparison logic for llama.cpp runtime release tags. A tag is the validated form <c>b&lt;number&gt;</c>
///     (regex <c>^b\d+$</c>, enforced at the transport boundary by <c>StoredNodeSettings.IsValidRecommendedLlamaCppTag</c>
///     and <c>NodeSettingsEndpointValidators</c>). Because the numeric suffix is monotonically increasing upstream, an
///     update is only "available" when the installed build is strictly OLDER than the recommended one — a string
///     inequality would falsely advertise a downgrade as an update when the installed tag is newer than the recommended
///     one (e.g. installed <c>b9700</c> vs recommended <c>b9692</c>).
/// </summary>
public static class LlamaCppRuntimeTag
{
    /// <summary>
    ///     Returns <see langword="true" /> when a newer llama.cpp runtime than the installed one is recommended.
    ///     <list type="bullet">
    ///         <item>No recommended tag (null/empty) → <see langword="false" /> (nothing to recommend).</item>
    ///         <item>
    ///             No installed tag (null/empty) → <see langword="true" />: a fresh node has nothing installed, so the
    ///             recommended build is offered as an install.
    ///         </item>
    ///         <item>
    ///             Both parse as <c>b&lt;number&gt;</c> → <c>installed &lt; recommended</c>. This never advertises a
    ///             downgrade: when the installed number is greater than or equal to the recommended one the result is
    ///             <see langword="false" />.
    ///         </item>
    ///         <item>
    ///             Either tag is an unexpected non-<c>b&lt;number&gt;</c> value → fall back to the prior behavior
    ///             (<c>!string.Equals(installed, recommended, Ordinal)</c>). This fallback can never throw on the hot GET
    ///             path and only ever IMPROVES the result for well-formed tags; malformed tags retain the original
    ///             "differs ⇒ update" semantics rather than risking an exception or silently hiding an update.
    ///         </item>
    ///     </list>
    /// </summary>
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
