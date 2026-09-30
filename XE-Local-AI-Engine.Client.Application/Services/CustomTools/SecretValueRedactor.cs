namespace XE_Local_AI_Engine.Client.Services.CustomTools;

using System.Text.RegularExpressions;

/// <summary>Value-based secret redaction for custom tools: every known secret VALUE, not name, is redacted.</summary>
/// <remarks>
///     The stock <c>AccessTokenQueryRedactor</c> only strips a named <c>access_token</c> query parameter, while a custom tool's secrets are
///     arbitrary operator-supplied values — secret header values, secret env values, secrets substituted into a URL or body. Every known
///     secret value is replaced with <c>[REDACTED]</c> before any string is logged or returned to the model, and URL userinfo is stripped too.
/// </remarks>
internal sealed partial class SecretValueRedactor
{
    private const string Placeholder = "[REDACTED]";

    // Longest-first so a secret that is a prefix of another does not leave a tail behind after the longer one is masked.
    private readonly IReadOnlyList<string> _secrets;

    public SecretValueRedactor(IEnumerable<string> secretValues)
    {
        ArgumentNullException.ThrowIfNull(secretValues);

        _secrets = secretValues
                   .Where(static value => !string.IsNullOrEmpty(value))
                   .Distinct(StringComparer.Ordinal)
                   .OrderByDescending(static value => value.Length)
                   .ToList();
    }

    /// <summary>
    ///     The values of at least <paramref name="minLength" /> characters, plus every whitespace-separated part of one that is
    ///     itself that long, so a <c>Bearer &lt;token&gt;</c> value also redacts the bare token wherever it appears alone.
    /// </summary>
    public static IEnumerable<string> WithParts(IEnumerable<string> values, int minLength)
    {
        ArgumentNullException.ThrowIfNull(values);
        // Whitespace parts catch "Bearer <token>"; the part after the first '=' catches "--api-key=<token>" (Codex review 2026-09-30).
        return values.SelectMany(static value => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Prepend(value))
                     .SelectMany(static part => part.IndexOf('=', StringComparison.Ordinal) is > 0 and var at && at < part.Length - 1
                         ? [part, part[(at + 1)..]]
                         : new[] { part })
                     .Where(value => value.Length >= minLength);
    }

    /// <summary>
    ///     Whether an environment key names the executable search path, which locates programs and is never a secret: redacting
    ///     it would blank the jail PATH out of the hint that tells an operator where a missing command was looked for.
    /// </summary>
    public static bool IsSearchPathKey(string key) => string.Equals(key, "PATH", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?<scheme>[a-zA-Z][a-zA-Z0-9+.\-]*://)[^/@\s]+@", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UserInfoRegex();

    /// <summary>
    ///     Returns <paramref name="text" /> with every known secret value replaced by <c>[REDACTED]</c> and any URL
    ///     userinfo (<c>scheme://user:pass@host</c> → <c>scheme://host</c>) stripped. Null/empty passes through.
    /// </summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = StripUserInfo(text);
        foreach (var secret in _secrets)
        {
            result = result.Replace(secret, Placeholder, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>Strips userinfo from any <c>scheme://user:pass@host</c> URL in <paramref name="text" />.</summary>
    public static string StripUserInfo(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return UserInfoRegex().Replace(text, "${scheme}");
    }
}
