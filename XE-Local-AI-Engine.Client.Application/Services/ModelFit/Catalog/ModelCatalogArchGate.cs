namespace XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;

using System.Globalization;

/// <summary>
///     Pure numeric comparison of llama.cpp <c>bNNNN</c> release tags, gating a catalog entry's
///     <see cref="ModelCatalogEntry.MinLlamaCppTag" /> against the node's installed-else-pinned runtime build. An entry
///     whose architecture support landed in a build newer than the node's runtime is excluded from recommendations —
///     never surfaced as a broken pick.
/// </summary>
/// <remarks>
///     Fails OPEN (treated as supported) whenever either tag cannot be parsed as <c>bNNNN</c> — an unparseable installed
///     tag must never silently hide every catalog entry, and an entry with no meaningful floor imposes no gate.
/// </remarks>
public static class ModelCatalogArchGate
{
    /// <summary>
    ///     <see langword="true" /> when <paramref name="installedOrPinnedTag" />'s build number is at or above
    ///     <paramref name="minLlamaCppTag" />'s, or when either tag does not parse as <c>bNNNN</c> (fail-open).
    /// </summary>
    public static bool Supports(string? installedOrPinnedTag, string? minLlamaCppTag)
    {
        var minNumber = ParseBNumber(minLlamaCppTag);
        if (minNumber is null)
        {
            return true;
        }

        var installedNumber = ParseBNumber(installedOrPinnedTag);
        return installedNumber is null || installedNumber.Value >= minNumber.Value;
    }

    /// <summary>Parses a <c>bNNNN</c> release tag (e.g. <c>b9692</c>) to its numeric build number, or <see langword="null" /> when malformed.</summary>
    public static int? ParseBNumber(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var trimmed = tag.Trim();
        if (trimmed.Length < 2 || (trimmed[0] != 'b' && trimmed[0] != 'B'))
        {
            return null;
        }

        return int.TryParse(trimmed.AsSpan(start: 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}
