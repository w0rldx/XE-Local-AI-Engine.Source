namespace XE_Local_AI_Engine.Client.Common;

using System.Text.RegularExpressions;

/// <summary>The credential patterns redacted from text shown live: Development's own <c>!</c>-prefixed secrets and labelled values.</summary>
/// <remarks>
///     Covered: URL userinfo, an <c>Authorization</c> value with its scheme, a bare <c>Bearer</c> token, an upper-case environment
///     name holding TOKEN, SECRET, KEY, PASSWORD or CREDENTIAL, and common lower-case labels. A label stays, its value does not; a
///     quoted value goes whole, quotes included, and an unterminated one to the end of its line.
///     Errs toward redacting: a non-secret that looks like one is lost from a live view, a secret never leaks into it.
/// </remarks>
internal static partial class CredentialRedaction
{
    private const string Redacted = "[redacted]";
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture;

    // A double- or single-quoted value through its closing quote (backslash escapes included; unterminated runs to the line end).
    private const string QuotedValue = @"""(?:[^""\\\r\n]|\\.)*""?|'(?:[^'\\\r\n]|\\.)*'?";
    private const string BareValue = @"[^\s,;""']+";

    /// <summary>The text with every credential pattern replaced by <c>[redacted]</c>; a labelled value keeps its label.</summary>
    /// <exception cref="RegexMatchTimeoutException">A pattern exceeded its match timeout.</exception>
    public static string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var safe = DevelopmentCredential().Replace(value, Redacted);
        safe = UrlUserInfo().Replace(safe, "${label}" + Redacted + "@");
        // The header before the bare token, so "Authorization: Bearer x" loses its scheme and token in one match.
        safe = AuthorizationValue().Replace(safe, "${label}" + Redacted);
        safe = BearerToken().Replace(safe, "${label} " + Redacted);
        safe = EnvironmentCredential().Replace(safe, "${label}" + Redacted);
        return CommonCredential().Replace(safe, "${label}" + Redacted);
    }

    [GeneratedRegex(@"![A-Za-z][A-Za-z0-9]{11,}", Options, 100)]
    private static partial Regex DevelopmentCredential();

    // scheme://user:pass@host or scheme://token@host; the scheme stays.
    [GeneratedRegex(@"(?i)(?<label>\b[a-z][a-z0-9+.-]*://)[^\s/@]+@", Options, 100)]
    private static partial Regex UrlUserInfo();

    // A quoted value carries its scheme inside the quotes: {"Authorization": "Basic x"}, 'Authorization': 'Bearer x'.
    [GeneratedRegex(@"(?i)(?<label>\bauthorization)[""']?\s*[:=]\s*(?:" + QuotedValue + @"|(?:(?:bearer|basic|token|digest)\s+)?" + BareValue + ")", Options, 100)]
    private static partial Regex AuthorizationValue();

    // "Bearer x" as well as a bearer label: bearer=x, bearer: x.
    [GeneratedRegex(@"(?i)(?<label>\bbearer)(?:\s*[:=]\s*|\s+)(?:" + QuotedValue + "|" + BareValue + ")", Options, 100)]
    private static partial Regex BearerToken();

    // Case-sensitive on purpose: GITHUB_TOKEN, KEY, TOKEN_VALUE, DB_PASSWORD; a lower-case "monkey: 3" is prose, not a secret.
    [GeneratedRegex(@"(?<label>\b(?:[A-Z][A-Z0-9_]*)?(?:TOKEN|SECRET|KEY|PASSWORD|PASSWD|CREDENTIALS?)[A-Z0-9_]*)[""']?\s*[:=]\s*(?:" + QuotedValue + "|" + BareValue + ")",
        Options,
        100)]
    private static partial Regex EnvironmentCredential();

    [GeneratedRegex(@"(?i)(?<label>[a-z0-9_.-]*(?:password|passwd|secret|token|api[-_]?key|access[-_]?key|private[-_]?key))[""']?\s*[:=]\s*(?:" + QuotedValue + "|"
                    + BareValue + ")",
        Options,
        100)]
    private static partial Regex CommonCredential();
}
