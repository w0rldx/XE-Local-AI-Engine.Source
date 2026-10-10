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
                     .SelectMany(WithAssignedValue)
                     .Where(value => value.Length >= minLength);
    }

    /// <summary>
    ///     The values to redact for a launched command: <paramref name="secretValues" /> (environment or header values) with their
    ///     whitespace parts, but each argument only as a whole token plus its after-<c>=</c> tail, and never a fully qualified path.
    /// </summary>
    /// <remarks>
    ///     An argument is already a token, so splitting it would blank every long word of a <c>cmd /c</c> script (Windows tester round 3);
    ///     a path is not a credential, the API returns arguments in plaintext anyway, and a missing-file error must show it.
    /// </remarks>
    public static IEnumerable<string> ForCommand(IEnumerable<string> secretValues, IEnumerable<string> arguments, int minLength)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return WithParts(secretValues, minLength)
            .Concat(arguments.SelectMany(static argument => Path.IsPathFullyQualified(argument) ? CredentialLikeParts(argument) : CredentialLikeParts(argument).Prepend(argument))
                             .SelectMany(WithAssignedValueOfAToken)
                             .Where(value => value.Length >= minLength));
    }

    // A multi-word script keeps its whole-argument entry only: splitting it at its first '=' would turn the rest of the
    // script into one "secret"; its own `--key=value` parts are handled by CredentialLikeParts.
    private static string[] WithAssignedValueOfAToken(string value) => value.Any(char.IsWhiteSpace) ? [value] : WithAssignedValue(value);

    private static readonly string[] CredentialSchemes = ["Bearer", "Basic", "Token"];

    /// <summary>
    ///     The whitespace parts of a script-like argument that look like a credential: a flag's value (<c>--token x</c>), an assigned or
    ///     header value (<c>--key=x</c>, <c>API_KEY=x</c>, <c>X-Api-Key: x</c>), the part after an auth scheme, or 16+ mixed characters.
    /// </summary>
    /// <remarks>
    ///     A token inside <c>cmd /c "srv --token sk-…"</c> or <c>"Authorization: Bearer sk-…"</c> is still redacted when the server
    ///     echoes it alone (security and Codex reviews, tester round 3); plain script words and flag names stay readable.
    /// </remarks>
    private static IEnumerable<string> CredentialLikeParts(string argument)
    {
        var parts = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var isFlag = part.Length > 1 && part[0] == '-';
            var followsFlag = !isFlag && i > 0 && parts[i - 1].Length > 1 && parts[i - 1][0] == '-' && !parts[i - 1].Contains('=', StringComparison.Ordinal);
            var followsScheme = i > 0 && CredentialSchemes.Contains(parts[i - 1], StringComparer.OrdinalIgnoreCase);
            var followsHeaderName = i > 0 && parts[i - 1].Length > 1 && parts[i - 1][^1] == ':';
            var looksLikeCredential = part.Length >= 16 && part.Any(char.IsDigit) && part.Any(char.IsLetter) && part.IndexOfAny(['/', '\\']) < 0;
            if (followsFlag || followsScheme || followsHeaderName || looksLikeCredential)
            {
                yield return part;
            }

            // An assignment ("--key=x", "set API_KEY=x") or a header without a space ("X-Api-Key:x") carries its value after the
            // separator; "://" is a URL, not a header.
            if (AssignedValue(part) is { } assigned)
            {
                yield return assigned;
            }
        }
    }

    private static string[] WithAssignedValue(string part) =>
        part.IndexOf('=', StringComparison.Ordinal) is > 0 and var at && at < part.Length - 1 ? [part, part[(at + 1)..]] : [part];

    private static string? AssignedValue(string part)
    {
        // A path is never an assignment: "C:\\probe\\x.dll" would otherwise make the whole path after the drive colon a secret.
        if (Path.IsPathFullyQualified(part) || IsDriveLetterPath(part))
        {
            return null;
        }

        var at = part.IndexOfAny(['=', ':']);
        if (at <= 0 || at >= part.Length - 1 || (part[at] == ':' && part.AsSpan(at + 1).StartsWith("//", StringComparison.Ordinal)))
        {
            return null;
        }

        return part[(at + 1)..];
    }

    private static bool IsDriveLetterPath(string part) =>
        part.Length > 2 && char.IsAsciiLetter(part[0]) && part[1] == ':' && part[2] is '\\' or '/';

    /// <summary>
    ///     Whether an environment key names the executable search path, which locates programs and is never a secret: redacting
    ///     it would blank the jail PATH out of the hint that tells an operator where a missing command was looked for.
    /// </summary>
    public static bool IsSearchPathKey(string key) =>
        string.Equals(key, "PATH", StringComparison.OrdinalIgnoreCase);

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
