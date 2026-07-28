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

    /// <summary>
    ///     Sanitizes captured command output. Unlike a reviewer submission, a redactable match here is REDACTED and
    ///     the evidence is kept, rather than rejecting the whole artifact.
    ///     <para>
    ///         The reason is that this text is machine-generated build and test output, not model prose, and the
    ///         scanner's keyword-free fallback flags any 32+ character run scoring 4.5 bits of Shannon entropy —
    ///         which ordinary identifiers reach. Measured against this repository's own test-method names, 237 of
    ///         2452 (9.7%) match on their own, so rejecting outright turned roughly one failing test in ten into
    ///         "contains credential-like material" instead of "validation failed", hiding the real result behind a
    ///         security error. Redaction is what <c>MemoryProposalSecretScanner</c> returns a redaction for.
    ///     </para>
    ///     <para>
    ///         This does not leak: a matched secret is replaced by its <c>[REDACTED:&lt;class&gt;]</c> marker, so it
    ///         is absent from the persisted artifact under either policy — the only difference is whether the rest
    ///         of the evidence survives with it. The unredactable cases (PEM private keys, Google service-account
    ///         JSON, a bare password-like value) still reject the whole artifact.
    ///     </para>
    /// </summary>
    internal static DevelopmentCommandEvidence Sanitize(DevelopmentCommandEvidence evidence, params string[] protectedRoots)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return evidence with
        {
            StandardOutput = SanitizeText(evidence.StandardOutput, allowRedaction: true, protectedRoots),
            StandardError = SanitizeText(evidence.StandardError, allowRedaction: true, protectedRoots)
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

    /// <summary>
    ///     Sanitizes model-authored artifact text. Any credential-like match rejects the whole artifact — a reviewer
    ///     submission has no business containing one, so there is nothing to salvage by redacting it.
    /// </summary>
    internal static string SanitizeText(string text, params string[] protectedRoots) =>
        SanitizeText(text, allowRedaction: false, protectedRoots);

    private static string SanitizeText(string text, bool allowRedaction, string[] protectedRoots)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Redact filesystem paths BEFORE the secret scan, not after.
        //
        // The scanner's keyword-free fallback rejects any delimited run of 32+ characters drawn from
        // [A-Za-z0-9+/=_-] whose Shannon entropy is >= 4.5. '/', '-' and '_' are all in that class, so an ordinary
        // absolute path is a single long run and a deep one clears the entropy bar on its own: measured here,
        // '<tmp>/<project>/tests/Probe/bin/Release/net10' scores 4.83. Scanning first therefore rejected the
        // artifact over a path that the very next lines were about to replace with the redaction marker anyway —
        // which made every `dotnet build` / `dotnet test` evidence record unpersistable, because those commands
        // print their output paths. That went unnoticed while the validation profile was a lone `git diff --check`,
        // whose output carries no paths.
        //
        // This does not weaken the secret policy. A path-shaped match is replaced wholesale by the marker, so a
        // credential hidden inside one is removed from the artifact either way — rejected before, redacted now. A
        // secret that is not path-shaped is untouched by these passes and still reaches the scan below: the Unix
        // pattern only fires on a '/' that is NOT preceded by an alphanumeric, so a base64 blob containing an
        // interior '/' is not mistaken for a path and is still caught by the entropy fallback.
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
        sanitized = UnixAbsolutePathRegex().Replace(sanitized, RedactedPath);

        var scan = MemoryProposalSecretScanner.Scan(string.Empty,
            string.Empty,
            sanitized,
            [],
            string.Empty);

        // Structurally unredactable: a PEM private key or a Google service-account JSON block IS its surrounding
        // context, so there is no safe partial form. Always rejects, for every caller.
        if (scan.ShouldReject)
        {
            throw new DevelopmentWorkspaceSecurityException("Development artifact content contains credential-like material and cannot be persisted.");
        }

        if (scan.RedactedContent is not null)
        {
            if (!allowRedaction)
            {
                throw new DevelopmentWorkspaceSecurityException("Development artifact content contains credential-like material and cannot be persisted.");
            }

            sanitized = scan.RedactedContent;
        }

        // Checked after redaction: a password-like value that sat inside a recognized assignment is already gone. A
        // bare one that survived has no redaction class of its own, so it still rejects the artifact.
        if (BarePasswordLikeValueRegex().IsMatch(sanitized))
        {
            throw new DevelopmentWorkspaceSecurityException("Development artifact content contains a bare password-like value and cannot be persisted.");
        }

        return sanitized;
    }
}
