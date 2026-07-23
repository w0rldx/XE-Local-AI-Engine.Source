namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

internal static partial class DevelopmentArtifactSanitizer
{
    private const string RedactedPath = "[REDACTED:development-path]";

    [GeneratedRegex(@"(?<![A-Za-z0-9])![A-Za-z][A-Za-z0-9]{11,}(?![A-Za-z0-9])", RegexOptions.ExplicitCapture, 2000)]
    private static partial Regex BarePasswordLikeValueRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9:/])/(?!/)[^\s\x00-\x1F\""'<>|]+", RegexOptions.ExplicitCapture, 2000)]
    private static partial Regex UnixAbsolutePathRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]:[\\/][^\s\x00-\x1F\""'<>|]+", RegexOptions.ExplicitCapture, 2000)]
    private static partial Regex WindowsAbsolutePathRegex();

    [GeneratedRegex(@"\\\\[^\s\\/]+[\\/][^\s\x00-\x1F\""'<>|]+", RegexOptions.ExplicitCapture, 2000)]
    private static partial Regex UncAbsolutePathRegex();

    internal static DevelopmentCommandEvidence Sanitize(DevelopmentCommandEvidence evidence, params string[] protectedRoots)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return evidence with
        {
            StandardOutput = SanitizeText(evidence.StandardOutput, protectedRoots),
            StandardError = SanitizeText(evidence.StandardError, protectedRoots)
        };
    }

    internal static DevelopmentReviewerSubmission Sanitize(DevelopmentReviewerSubmission submission, params string[] protectedRoots)
    {
        ArgumentNullException.ThrowIfNull(submission);
        return submission with
        {
            Summary = SanitizeText(submission.Summary, protectedRoots),
            Findings = submission.Findings
                                 .Select(finding => finding with
                                 {
                                     Category = SanitizeText(finding.Category, protectedRoots),
                                     Summary = SanitizeText(finding.Summary, protectedRoots)
                                 })
                                 .ToArray()
        };
    }

    internal static string SanitizeText(string text, params string[] protectedRoots)
    {
        ArgumentNullException.ThrowIfNull(text);
        var scan = MemoryProposalSecretScanner.Scan(string.Empty,
            string.Empty,
            text,
            [],
            string.Empty);
        if (scan.ShouldReject || scan.RedactedContent is not null)
        {
            throw new DevelopmentWorkspaceSecurityException("Development artifact content contains credential-like material and cannot be persisted.");
        }

        if (BarePasswordLikeValueRegex().IsMatch(text))
        {
            throw new DevelopmentWorkspaceSecurityException("Development artifact content contains a bare password-like value and cannot be persisted.");
        }

        var sanitized = text;
        foreach (var root in protectedRoots.Where(static root => !string.IsNullOrWhiteSpace(root) && root.Length > 1)
                                           .Distinct(StringComparer.OrdinalIgnoreCase)
                                           .OrderByDescending(static root => root.Length))
        {
            sanitized = sanitized.Replace(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                RedactedPath,
                StringComparison.OrdinalIgnoreCase);
        }

        sanitized = WindowsAbsolutePathRegex().Replace(sanitized, RedactedPath);
        sanitized = UncAbsolutePathRegex().Replace(sanitized, RedactedPath);
        return UnixAbsolutePathRegex().Replace(sanitized, RedactedPath);
    }
}
