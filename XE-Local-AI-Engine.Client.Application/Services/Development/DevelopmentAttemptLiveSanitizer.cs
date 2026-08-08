namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class DevelopmentAttemptLiveSanitizer
{
    private const string Redacted = "[redacted]";

    public static DevelopmentAttemptLiveUpdate Sanitize(DevelopmentAttemptLiveUpdate update, int maxTextCharacters)
    {
        ArgumentNullException.ThrowIfNull(update);
        return update with
        {
            ModelId = Identifier(update.ModelId) ?? "unknown",
            Provider = Identifier(update.Provider) ?? "unknown",
            OutputDelta = Text(update.OutputDelta, maxTextCharacters),
            CurrentActivity = Text(update.CurrentActivity, maxTextCharacters),
            CurrentToolId = Identifier(update.CurrentToolId),
            CurrentCommandId = Identifier(update.CurrentCommandId),
            SubjectHash = Fingerprint(update.SubjectHash),
            WarningMessage = Text(update.WarningMessage, maxTextCharacters)
        };
    }

    public static string StableFingerprint(string category, string? value)
    {
        var safeCategory = Identifier(category) ?? string.Empty;
        var safeValue = Text(value, 4096) ?? string.Empty;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{safeCategory}\n{safeValue}")));
    }

    private static string? Identifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var safe = Text(value, 128)!;
        return IdentifierUnsafeCharacters().Replace(safe, "_");
    }

    private static string? Fingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return StableFingerprint("subject", value);
    }

    private static string? Text(string? value, int maxCharacters)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var safe = DevelopmentCredential().Replace(value, Redacted);
        safe = CommonCredential().Replace(safe, "${label}" + Redacted);
        safe = WindowsAbsolutePath().Replace(safe, "[path]");
        safe = UnixAbsolutePath().Replace(safe, "[path]");
        return safe.Length <= maxCharacters ? safe : safe[..maxCharacters];
    }

    [GeneratedRegex(@"![A-Za-z][A-Za-z0-9]{11,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture, 100)]
    private static partial Regex DevelopmentCredential();

    [GeneratedRegex(@"(?i)(?<label>password|passwd|secret|api[-_]?key|authorization|bearer)\s*[:=]\s*[^\s,;]+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture, 100)]
    private static partial Regex CommonCredential();

    [GeneratedRegex("""(?:[A-Za-z]:\\|\\\\)[^\r\n\t"']+""", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture, 100)]
    private static partial Regex WindowsAbsolutePath();

    [GeneratedRegex("""/(?:[^\s/]+/)+[^\s,;:"']*""", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture, 100)]
    private static partial Regex UnixAbsolutePath();

    [GeneratedRegex(@"[^A-Za-z0-9._:/-]", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture, 100)]
    private static partial Regex IdentifierUnsafeCharacters();
}
