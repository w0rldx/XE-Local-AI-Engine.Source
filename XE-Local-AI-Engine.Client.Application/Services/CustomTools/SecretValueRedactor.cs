namespace XE_Local_AI_Engine.Client.Services.CustomTools;

using System.Text.RegularExpressions;

/// <summary>
///     Value-based secret redaction for custom tools. The stock <c>AccessTokenQueryRedactor</c> only strips a named
///     <c>access_token</c> query parameter; a custom tool's secrets are arbitrary operator-supplied values (secret
///     header values, secret env values, secrets a user substitutes into a URL or body), so redaction must key off the
///     VALUES, not names. Every known secret value is replaced with <c>[REDACTED]</c> before any string is logged or
///     returned to the model, and URL userinfo is stripped as well.
/// </summary>
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
