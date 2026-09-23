namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

using Velopack;

/// <summary>
///     The one place outside <see cref="PaginatingGithubSource" /> that touches Velopack's version type, so the update
///     service and its tests stay Velopack-free.
/// </summary>
internal static class AppUpdateVersions
{
    /// <summary>
    ///     True when <paramref name="candidate" /> parses and is strictly greater than <paramref name="incumbent" />.
    ///     An absent or unparseable incumbent loses to any parseable candidate.
    /// </summary>
    /// <remarks>
    ///     The comparison is <see cref="SemanticVersion" />'s <c>CompareTo</c>, which is prerelease-aware — NOT
    ///     <c>CompareByVersion</c>, which ignores the prerelease label and would rank
    ///     <c>1.0.0-rc.2.dev.20260922.1</c> equal to <c>1.0.0-rc.2</c>.
    /// </remarks>
    internal static bool IsHigher(string? candidate, string? incumbent)
    {
        if (!SemanticVersion.TryParse(candidate, out var candidateVersion))
        {
            return false;
        }

        return !SemanticVersion.TryParse(incumbent, out var incumbentVersion) || candidateVersion > incumbentVersion;
    }

    /// <summary>True when the version carries a prerelease label; false for an absent or unparseable value.</summary>
    internal static bool IsPrerelease(string? version)
    {
        return SemanticVersion.TryParse(version, out var parsed) && parsed.IsPrerelease;
    }

    /// <summary>The highest non-prerelease full-package version in a feed, or null when it holds none.</summary>
    internal static string? NewestStable(IReadOnlyList<VelopackAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        return assets.Where(static asset => asset.Type == VelopackAssetType.Full && !asset.Version.IsPrerelease)
                     .MaxBy(static asset => asset.Version)
                     ?.Version.ToString();
    }
}
