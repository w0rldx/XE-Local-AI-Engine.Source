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

    /// <summary>How many release notes the snapshot keeps: the running version plus the newest updates.</summary>
    internal const int MaxReleaseNotes = 3;

    /// <summary>The per-version cap on notes markdown, so one huge release body cannot bloat the response.</summary>
    internal const int MaxNotesLength = 16_000;

    /// <summary>One note per version that carries packaged notes, read from the full-package entries of a feed.</summary>
    internal static IReadOnlyList<AppReleaseNote> ReleaseNotes(IReadOnlyList<VelopackAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        return
        [
            .. assets.Where(static asset => asset.Type == VelopackAssetType.Full && !string.IsNullOrWhiteSpace(asset.NotesMarkdown))
                     .DistinctBy(static asset => asset.Version)
                     .Select(static asset => Capped(asset.Version.ToString(), asset.NotesMarkdown))
        ];
    }

    /// <summary>
    ///     The running version's note first, then newer versions newest first, at most <see cref="MaxReleaseNotes" />.
    ///     Older versions and unparseable ones are dropped; a version seen on two feeds counts once.
    /// </summary>
    internal static IReadOnlyList<AppReleaseNote> SelectReleaseNotes(IEnumerable<AppReleaseNote> notes, string currentVersion)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (!SemanticVersion.TryParse(currentVersion, out var running))
        {
            return [];
        }

        var kept = notes.Where(note => VersionOf(note) is { } version && version >= running)
                        .DistinctBy(VersionOf)
                        .ToList();
        var runningNote = kept.Where(note => VersionOf(note) == running);
        var newer = kept.Where(note => VersionOf(note) > running)
                        .OrderByDescending(VersionOf)
                        .Select(static note => note with { IsNewerThanRunning = true });
        return [.. runningNote.Concat(newer).Take(MaxReleaseNotes)];
    }

    private static SemanticVersion? VersionOf(AppReleaseNote note) =>
        SemanticVersion.TryParse(note.Version, out var version) ? version : null;

    private static AppReleaseNote Capped(string version, string markdown)
    {
        if (markdown.Length <= MaxNotesLength)
        {
            return new AppReleaseNote { Version = version, NotesMarkdown = markdown };
        }

        // Never cut between the two halves of a surrogate pair.
        var length = char.IsHighSurrogate(markdown[MaxNotesLength - 1]) ? MaxNotesLength - 1 : MaxNotesLength;
        return new AppReleaseNote { Version = version, NotesMarkdown = markdown[..length], IsTruncated = true };
    }
}
