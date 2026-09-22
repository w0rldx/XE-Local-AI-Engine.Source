namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Text.RegularExpressions;

/// <summary>
///     A bounded, sanitized ring of the last few lines the child wrote to <c>stderr</c>, so a crash during model load
///     can be reported with the runtime's own words instead of a fixed sentence.
/// </summary>
/// <remarks>
///     Bounded twice (line count and total characters) because a wedged server can write without limit. Only stderr is
///     collected, and it is surfaced only when the child died BEFORE readiness — no generation has run at that point,
///     so the privacy-sensitive prompt echo the launcher keeps at Debug cannot appear in the tail. Absolute paths are
///     reduced to their file name on the way in, so the tail carries no directory layout wherever it later travels.
/// </remarks>
internal sealed partial class ImageServerStderrTail
{
    private const int MaxCharacters = 4096;
    private const int MaxLines = 20;

    private const string Replacement = "${leaf}";

    // A directory segment: no separator, no colon (invalid in a Windows name, and it keeps a segment from swallowing
    // the next path's drive root), and spaces only inside it, so "Jane Doe" survives but trailing prose does not.
    private const string Segment = @"[^\\/:\s""'<>|\r\n](?:[^\\/:""'<>|\r\n]*[^\\/:\s""'<>|\r\n])?";

    // The final segment, all that is kept. It stops at whitespace or a quote, so the words after a path stay.
    private const string Leaf = @"(?<leaf>[^\s\\/""'<>|]*)";

    // Every pattern carries a 1s match timeout, which bounds the parse against pathological input.
    private const int MatchTimeout = 1000;

    private readonly Lock _gate = new();
    private readonly Queue<string> _lines = new();
    private int _characters;

    /// <summary>Appends one sanitized line, evicting the oldest lines until both bounds hold again.</summary>
    public void Append(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var sanitized = Sanitize(line.Trim());
        lock (_gate)
        {
            _lines.Enqueue(sanitized);
            _characters += sanitized.Length;
            while (_lines.Count > MaxLines || (_characters > MaxCharacters && _lines.Count > 1))
            {
                _characters -= _lines.Dequeue().Length;
            }
        }
    }

    /// <summary>The retained lines joined by <c>" | "</c>, or <see langword="null" /> when the child wrote nothing.</summary>
    public string? Snapshot()
    {
        lock (_gate)
        {
            return _lines.Count == 0 ? null : string.Join(" | ", _lines);
        }
    }

    /// <summary>Reduces every absolute path in the line to its file name; pure, so it is directly assertable.</summary>
    internal static string Sanitize(string line)
    {
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
