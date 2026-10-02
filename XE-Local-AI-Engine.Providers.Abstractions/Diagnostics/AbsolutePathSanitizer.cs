namespace XE_Local_AI_Engine.Providers.Abstractions.Diagnostics;

using System.Text.RegularExpressions;

/// <summary>
///     Reduces drive-rooted, UNC and POSIX absolute paths in free text to their final segment, so a line carries no
///     directory layout. URLs and relative paths are left alone; every pattern has a 1 s match timeout.
/// </summary>
public static partial class AbsolutePathSanitizer
{
    private const string Replacement = "${leaf}";

    // A directory segment: no separator, no colon (invalid in a Windows name, and it keeps a segment from swallowing
    // the next path's drive root), and spaces only inside it, so "Jane Doe" survives but trailing prose does not.
    private const string Segment = @"[^\\/:\s""'<>|\r\n](?:[^\\/:""'<>|\r\n]*[^\\/:\s""'<>|\r\n])?";

    // The final segment, all that is kept. It stops at whitespace or a quote, so the words after a path stay.
    private const string Leaf = @"(?<leaf>[^\s\\/""'<>|]*)";

    // Every pattern carries a 1s match timeout, which bounds the parse against pathological input.
    private const int MatchTimeout = 1000;

    /// <summary>Reduces every absolute path in the line to its file name.</summary>
    public static string Sanitize(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var sanitized = DriveRootedPathRegex().Replace(line, Replacement);
        sanitized = UncPathRegex().Replace(sanitized, Replacement);
        return PosixPathRegex().Replace(sanitized, Replacement);
    }

    // A drive-rooted path. The lookbehind keeps a URL scheme ("https:") from reading as a drive letter.
    [GeneratedRegex(@"(?<![A-Za-z])[A-Za-z]:[\\/](?:" + Segment + @"[\\/])+" + Leaf,
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: MatchTimeout)]
    private static partial Regex DriveRootedPathRegex();

    // A UNC path: server, share, then optional directories. The "//server/share" form is out of scope.
    [GeneratedRegex(@"\\\\[^\s\\/""'<>|]+[\\/](?:" + Segment + @"[\\/])*" + Leaf,
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: MatchTimeout)]
    private static partial Regex UncPathRegex();

    // A POSIX path, segments read exactly as above. The lookbehind leaves URLs and relative paths alone.
    [GeneratedRegex(@"(?<![:\w/])/(?:" + Segment + @"/)+" + Leaf,
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: MatchTimeout)]
    private static partial Regex PosixPathRegex();
}
