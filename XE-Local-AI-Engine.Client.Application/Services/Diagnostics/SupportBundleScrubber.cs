namespace XE_Local_AI_Engine.Client.Services.Diagnostics;

using System.Text;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Providers.Abstractions.Diagnostics;

/// <summary>Removes personal and secret-shaped content from every text entry of a support bundle.</summary>
/// <remarks>
///     Home and data-root prefixes become <c>~</c> / <c>&lt;data&gt;</c>, other absolute paths their leaf, then e-mail
///     and token shapes (JWT, <c>sk-</c>, GitHub, <c>Bearer</c>, dense letter+digit runs) and private or link-local
///     IPv4 addresses (the port is kept; loopback is kept) are masked. Hex and timestamp
///     segments (trace ids, SHAs, 20260913T134250Z), paths under a mapped root and model or category names are kept; a
///     bare user or host name and a hex-only secret are not covered. Line by line, 1 s regex timeouts; idempotent.
/// </remarks>
public sealed partial class SupportBundleScrubber
{
    public const string HomeMarker = "~";
    public const string DataMarker = "<data>";
    public const string RedactedEmail = "[redacted-email]";
    public const string RedactedToken = "[redacted-token]";
    public const string RedactedIp = "[redacted-ip]";
    public const string DroppedLine = "[line removed: scrubber timeout]";

    private const int MatchTimeoutMilliseconds = 1000;
    private const int DenseSegmentLength = 16;

    // Random keys carry many digits; an identifier such as AddBenchmarkP2Discrimination carries one or two.
    private const int MinSecretDigits = 4;

    // Word-character sentinels: the path regexes never start a match right after a word character, so a prefix replaced
    // by one of these keeps its tail out of the leaf reduction; both are swapped for the visible markers afterwards.
    private const string HomeSentinel = "XEScrubHomeQ";
    private const string DataSentinel = "XEScrubDataQ";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(MatchTimeoutMilliseconds);

    private readonly Regex? _prefixRegex;
    private readonly Dictionary<string, string> _sentinelByPrefix;

    /// <param name="userProfileDirectories">The user's home directories, mapped to <c>~</c>.</param>
    /// <param name="dataRoot">The node data root (<c>INodeDataDirectory.Root</c>), mapped to <c>&lt;data&gt;</c>.</param>
    /// <param name="ignoreCase">Whether prefixes match case-insensitively; <see langword="true" /> on Windows.</param>
    public SupportBundleScrubber(IEnumerable<string?> userProfileDirectories, string? dataRoot, bool ignoreCase)
    {
        ArgumentNullException.ThrowIfNull(userProfileDirectories);
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _sentinelByPrefix = new Dictionary<string, string>(comparer);
        AddPrefix(dataRoot, DataSentinel);
        foreach (var directory in userProfileDirectories)
        {
            AddPrefix(directory, HomeSentinel);
        }

        if (_sentinelByPrefix.Count == 0)
        {
            return;
        }

        // Longest first, so a data root under the home directory wins over the home prefix. Each separator matches
        // either slash style, and the prefix must end at a separator or a non-name character.
        var alternatives = _sentinelByPrefix.Keys
                                            .OrderByDescending(static prefix => prefix.Length)
                                            .Select(static prefix => string.Join(@"[\\/]", prefix.Split('/').Select(Regex.Escape)));
        var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        _prefixRegex = new Regex(@"(?<![\w.\-])(?:" + string.Join('|', alternatives) + @")(?![\w.\-])", options, MatchTimeout);
    }

    /// <summary>Returns <paramref name="text" /> with paths, e-mail addresses and token-shaped values scrubbed.</summary>
    public string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        var lines = text.Split('\n');
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                builder.Append('\n');
            }

            builder.Append(ScrubLine(lines[index]));
        }

        return builder.ToString();
    }

    private void AddPrefix(string? directory, string sentinel)
    {
        // Keyed with '/' separators; the regex matches either style and the lookup normalizes the same way.
        var normalized = directory?.Trim().Replace('\\', '/').TrimEnd('/');

        // A bare root ("/", "C:") or a single segment would rewrite unrelated text; it is not a usable prefix.
        if (normalized is null || normalized.Split('/').Count(static segment => segment.Length > 0) < 2)
        {
            return;
        }

        _sentinelByPrefix.TryAdd(normalized, sentinel);
    }

    private string ScrubLine(string line)
    {
        if (line.Length == 0)
        {
            return line;
        }

        try
        {
            // Already-scrubbed markers become sentinels too, so a second pass leaves "~/x/y" alone (idempotence).
            var scrubbed = ExistingHomeMarkerRegex().Replace(line, HomeSentinel);
            scrubbed = ExistingDataMarkerRegex().Replace(scrubbed, DataSentinel);
            if (_prefixRegex is not null)
            {
                scrubbed = _prefixRegex.Replace(scrubbed, match => _sentinelByPrefix[match.Value.Replace('\\', '/')]);
            }

            scrubbed = AbsolutePathSanitizer.Sanitize(scrubbed);
            scrubbed = scrubbed.Replace(HomeSentinel, HomeMarker, StringComparison.Ordinal)
                               .Replace(DataSentinel, DataMarker, StringComparison.Ordinal);
            scrubbed = EmailRegex().Replace(scrubbed, RedactedEmail);
            scrubbed = BearerRegex().Replace(scrubbed, "Bearer " + RedactedToken);
            scrubbed = KnownTokenRegex().Replace(scrubbed, RedactedToken);
            scrubbed = AssignedSecretRegex().Replace(scrubbed, "${name}${separator}${quote}" + RedactedToken + "${quote}");
            scrubbed = JwtRegex().Replace(scrubbed, static match => ContainsDigit(match.Value) ? RedactedToken : match.Value);
            scrubbed = PrivateIpv4Regex().Replace(scrubbed, RedactedIp);
            var text = scrubbed;
            return TokenRunRegex().Replace(text, match => IsUnderScrubbedPrefix(text, match.Index) || !IsSecretShaped(match.Value) ? match.Value : RedactedToken);
        }
        catch (RegexMatchTimeoutException)
        {
            return DroppedLine;
        }
    }

    private static bool ContainsDigit(string value)
    {
        return value.Any(char.IsAsciiDigit);
    }

    // A run right after "~" or "<data>" is the rest of a path whose root was already mapped; it is never a secret.
    private static bool IsUnderScrubbedPrefix(string text, int index)
    {
        var before = text.AsSpan(0, index);
        return before.EndsWith(HomeMarker, StringComparison.Ordinal) || before.EndsWith(DataMarker, StringComparison.Ordinal);
    }

    // Secret-shaped: a '-', '_', '/', '+' or '=' separated segment of 16+ letters with 4+ digits that is neither hex
    // (trace id, SHA) nor a compact timestamp (digits with 'T' and/or 'Z', 20260913T134250067Z).
    private static bool IsSecretShaped(string run)
    {
        foreach (var segment in run.Split('-', '_', '/', '+', '='))
        {
            if (segment.Length >= DenseSegmentLength
                && segment.Any(char.IsAsciiLetter)
                && segment.Count(char.IsAsciiDigit) >= MinSecretDigits
                && !segment.All(char.IsAsciiHexDigit)
                && !IsCompactTimestamp(segment))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCompactTimestamp(string segment)
    {
        var letters = segment.Count(char.IsAsciiLetter);
        return letters <= 2 && segment.All(static ch => char.IsAsciiDigit(ch) || ch is 'T' or 'Z' or 't' or 'z');
    }

    [GeneratedRegex(@"(?<![\w~])~(?=[\\/])", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex ExistingHomeMarkerRegex();

    [GeneratedRegex(@"<data>", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex ExistingDataMarkerRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, MatchTimeoutMilliseconds)]
    private static partial Regex BearerRegex();

    // Prefixed keys (shapes from MemoryProposalSecretScanner plus Hugging Face and Google): OpenAI sk-, GitHub, Hugging
    // Face hf_, AWS access key ids, Slack xox*-, Google AIza, Azure AccountKey=.
    [GeneratedRegex(@"\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|hf_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}"
                    + @"|xox[abprs]-[A-Za-z0-9-]{10,}|AIza[0-9A-Za-z_-]{35}|AccountKey=[A-Za-z0-9+/=]{20,})",
        RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex KnownTokenRegex();

    // A value assigned to a secret-named key: bare, JSON or YAML style, the key and the value optionally quoted. A quoted
    // value keeps its quotes; an already-masked value never matches again.
    [GeneratedRegex(@"\b(?<name>api[_-]?key|secret|password|connectionstring|client_secret|access_token|refresh_token)(?<separator>[""']?\s*[=:]\s*)"
                    + @"(?:(?<quote>[""'])(?!\[redacted-token\])(?:(?!\k<quote>)[^\r\n]){4,}\k<quote>|[^\s,;{}""'\[]{4,})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, MatchTimeoutMilliseconds)]
    private static partial Regex AssignedSecretRegex();

    // A JWT-shaped triple of base64url segments. Masked only when it carries a digit, so dotted log categories stay.
    [GeneratedRegex(@"[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex JwtRegex();

    // RFC 1918 and link-local IPv4 name the operator's network; loopback, public addresses and the ":port" stay. The
    // guards keep a longer dotted run (a version such as 10.0.19041.1) from matching in part.
    [GeneratedRegex(@"(?<![\w.])(?:10\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])|192\.168|169\.254)\.\d{1,3}\.\d{1,3}(?!\.?\d)",
        RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex PrivateIpv4Regex();

    // A delimited run of 20+ token-alphabet characters; IsSecretShaped decides.
    [GeneratedRegex(@"(?<![A-Za-z0-9+/=_-])[A-Za-z0-9+/=_-]{20,}(?![A-Za-z0-9+/=_-])", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex TokenRunRegex();
}
