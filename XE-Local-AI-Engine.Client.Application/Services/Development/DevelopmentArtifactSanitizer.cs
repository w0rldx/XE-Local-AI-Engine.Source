namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

internal static partial class DevelopmentArtifactSanitizer
{
    private const string RedactedPath = "[REDACTED:development-path]";

    /// <summary>
    ///     How many trailing path segments a generic redaction keeps; what survives is a relative tail carrying no
    ///     host layout.
    /// </summary>
    /// <remarks>
    ///     Host identity lives in the leading segments — <c>/home/&lt;user&gt;</c>, <c>C:\Users\&lt;user&gt;</c>,
    ///     <c>/run/user/&lt;uid&gt;</c> — so a path too shallow to leave a tail behind them collapses to the bare
    ///     marker, as <c>/etc/passwd</c> and <c>/home/dev</c> do. A tail is kept at all because replacing the whole
    ///     path leaves a fault whose entire diagnosis is the directory unidentifiable: a container on a read-only
    ///     root fails every <c>dotnet</c> invocation with an EROFS <c>mkdir</c> and never says which directory.
    /// </remarks>
    private const int PreservedTailSegments = 2;

    private const int AlwaysRedactedLeadingSegments = 2;

    private static readonly char[] PathSeparators = ['/', '\\'];

    /// <summary>
    ///     Segments whose immediate successor names a principal (a user account or a uid), wherever they occur in the
    ///     path.
    /// </summary>
    /// <remarks>
    ///     The preserved tail may never begin at or before that successor, which is what makes the redaction
    ///     depth-independent: <c>/home/&lt;user&gt;</c> puts the principal second, <c>/run/user/&lt;uid&gt;</c> third
    ///     and WSL's <c>/mnt/c/Users/&lt;user&gt;</c> fourth, so a purely positional rule preserves the last of those
    ///     verbatim.
    /// </remarks>
    private static readonly HashSet<string> IdentityContainerSegments =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "home",
            "Users",
            "user"
        };

    [GeneratedRegex(@"(?<![A-Za-z0-9])![A-Za-z][A-Za-z0-9]{11,}(?![A-Za-z0-9])", RegexOptions.ExplicitCapture, 2000)]
    private static partial Regex BarePasswordLikeValueRegex();

    /// <summary>
    ///     Matches a Unix absolute path, skipping one the targeted pass has already replaced with the marker.
    /// </summary>
    /// <remarks>
    ///     The marker lookbehind is load-bearing: without it the '/' straight after a redaction marker matches and the
    ///     workspace-relative file and line go too, collapsing a compiler diagnostic to two adjacent markers. The
    ///     pattern exempts no SHAPE — a glob exemption for a '/' after <c>*</c> or <c>**</c> also carries
    ///     <c>**/home/alice/private</c> intact across the boundary a cloud provider is on. Known-safe literals are
    ///     protected by placeholder in <see cref="SanitizePromptText" /> instead, where the exact strings are known.
    /// </remarks>
    [GeneratedRegex(@"(?<![A-Za-z0-9:/])(?<!\[REDACTED:development-path\])/(?!/)[^\s\x00-\x1F\""'<>|]+", RegexOptions.ExplicitCapture, 2000)]
    private static partial Regex UnixAbsolutePathRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<!\[REDACTED:development-path\])[A-Za-z]:[\\/][^\s\x00-\x1F\""'<>|]+", RegexOptions.ExplicitCapture, 2000)]
    private static partial Regex WindowsAbsolutePathRegex();

    [GeneratedRegex(@"\\\\[^\s\\/]+[\\/][^\s\x00-\x1F\""'<>|]+", RegexOptions.ExplicitCapture, 2000)]
    private static partial Regex UncAbsolutePathRegex();

    /// <summary>
    ///     Replaces one matched absolute path with the marker plus, where the path is deep enough for the remainder to
    ///     carry no host identity, its trailing segments. See <see cref="PreservedTailSegments" /> for why.
    /// </summary>
    internal static string RedactAbsolutePath(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // A trailing separator would otherwise consume one of the walk-back steps below on an empty segment.
        var trimmed = value.TrimEnd(PathSeparators);
        var trailingSeparators = value[trimmed.Length..];
        var segments = trimmed.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);

        // A Windows drive letter is not a segment that can name anybody: counting "C:" would shift the identity
        // segment of "C:\Users\<user>\..." into the preserved tail, which is the one thing this must never do.
        var namedSegmentCount = segments.Length > 0 && segments[0].Length == 2 && segments[0][1] == ':' && char.IsAsciiLetter(segments[0][0])
            ? segments.Length - 1
            : segments.Length;

        // A positional rule alone leaks: `/mnt/c/Users/<user>` puts the username fourth and `/run/user/<uid>` puts the
        // uid third. Anchor on the container — the segment after it names a principal, so the tail starts past it.
        var driveOffset = segments.Length - namedSegmentCount;
        var minimumTailStart = AlwaysRedactedLeadingSegments;
        for (var index = 0; index < segments.Length; index++)
        {
            if (!IdentityContainerSegments.Contains(segments[index]))
            {
                continue;
            }

            // The principal is index + 1; the tail must start strictly after it. Expressed in NAMED-segment space so a
            // leading drive letter cannot shift the boundary.
            minimumTailStart = Math.Max(minimumTailStart, index - driveOffset + 2);
        }

        var available = namedSegmentCount - minimumTailStart;
        var keep = Math.Min(PreservedTailSegments, available);
        if (keep <= 0)
        {
            return RedactedPath;
        }

        var cut = trimmed.Length;
        for (var index = 0; index < keep; index++)
        {
            cut = trimmed.LastIndexOfAny(PathSeparators, cut - 1);
            if (cut <= 0)
            {
                return RedactedPath;
            }
        }

        // The original separator run is kept verbatim so a Windows tail still reads as a Windows tail.
        return RedactedPath + trimmed[cut..] + trailingSeparators;
    }

    /// <summary>
    ///     The roots a Development artifact's targeted redaction must cover: the three host roots, plus every root the
    ///     same directories are known by inside the sandbox.
    /// </summary>
    /// <remarks>
    ///     The sandbox half is not belt and braces: a command running in a container prints container-internal paths,
    ///     which match no host root, so targeted redaction becomes a silent no-op, the generic pattern fires instead
    ///     and the whole diagnostic collapses to one undifferentiated marker. Under the process provider the two
    ///     halves are identical and the duplicates are discarded, so this is one code path for both providers.
    /// </remarks>
    internal static string[] ResolveProtectedRoots(string repositoryRoot, DevelopmentWorkspaceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var roots = new List<string>
        {
            repositoryRoot,
            session.HostWorktreePath,
            session.RuntimePath
        };
        roots.AddRange(session.SandboxHandle.Mounts.Select(static mount => mount.SandboxPath));

        // The runtime mount ROOT as well as each mounted subdirectory: a build prints ".../xe-runtime/nuget/..." but it
        // also prints the parent, and redacting only the leaves would leave the parent to the generic pattern.
        roots.AddRange(session.SandboxHandle.Mounts
                              .Select(static mount => mount.SandboxPath)
                              .Select(static path => path[..Math.Max(path.LastIndexOf('/'), val2: 0)])
                              .Where(static parent => parent.Length > 1));

        return [.. roots.Where(static root => !string.IsNullOrWhiteSpace(root))];
    }

    /// <summary>
    ///     Sanitizes captured command output, redacting a matched secret and keeping the evidence rather than
    ///     rejecting the whole artifact.
    /// </summary>
    /// <remarks>
    ///     This text is machine-generated build and test output, not model prose, and the scanner's keyword-free
    ///     fallback flags any 32+ character run scoring 4.5 bits of Shannon entropy — which ordinary identifiers
    ///     reach: 237 of this repository's 2452 test-method names (9.7%) match on their own. A matched secret is
    ///     replaced by its <c>[REDACTED:&lt;class&gt;]</c> marker, so nothing leaks either way; the unredactable
    ///     cases (PEM private keys, Google service-account JSON, a bare password-like value) still reject outright.
    /// </remarks>
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
    ///     Sanitizes an engine-authored prompt, redacting rather than rejecting.
    /// </summary>
    /// <remarks>
    ///     A prompt is assembled here from the task title, its requirements, the base commit, the carried-file list
    ///     and the operator's instruction — text that legitimately carries absolute paths and long hashes — so
    ///     rejecting on a match loses exactly the prompts most worth recording. Each literal in
    ///     <paramref name="preservedGlobs" /> survives verbatim: the generic Unix pass fires on the '/' inside
    ///     <c>**/*Tests.cs</c>, and only this layer knows which strings are glob syntax rather than a path.
    /// </remarks>
    internal static string SanitizePromptText(string text, IReadOnlyCollection<string> preservedGlobs, params string[] protectedRoots)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(preservedGlobs);

        // Longest first: '**/*.Tests/**/*.cs' must claim its own text before '**/*.cs' can eat the tail of it.
        var literals = preservedGlobs.Where(static glob => !string.IsNullOrWhiteSpace(glob))
                                     .Distinct(StringComparer.Ordinal)
                                     .OrderByDescending(static glob => glob.Length)
                                     .ThenBy(static glob => glob, StringComparer.Ordinal)
                                     .ToArray();

        var masked = text;
        for (var index = 0; index < literals.Length; index++)
        {
            masked = masked.Replace(literals[index], Placeholder(index), StringComparison.Ordinal);
        }

        var sanitized = SanitizeText(masked, allowRedaction: true, protectedRoots);
        for (var index = 0; index < literals.Length; index++)
        {
            sanitized = sanitized.Replace(Placeholder(index), literals[index], StringComparison.Ordinal);
        }

        return sanitized;
    }

    /// <summary>
    ///     A stand-in that no pass in <see cref="SanitizeText(string, bool, string[])" /> can match.
    /// </summary>
    /// <remarks>
    ///     The delimiters are Unicode private-use characters, so the run carries no '/', no drive letter and none of
    ///     the <c>[A-Za-z0-9+/=_-]</c> alphabet the secret scanner's entropy fallback measures; only the digits
    ///     between them vary, and the delimiters stop them merging with neighbouring text.
    /// </remarks>
    private static string Placeholder(int index) =>
        $"\uE000{index}\uE001";

    /// <summary>
    ///     Sanitizes model-authored artifact text. Any credential-like match rejects the whole artifact — a reviewer
    ///     submission has no business containing one, so there is nothing to salvage by redacting it.
    /// </summary>
    internal static string SanitizeText(string text, params string[] protectedRoots) =>
        SanitizeText(text, allowRedaction: false, protectedRoots);

    /// <summary>
    ///     Replaces protected roots and absolute paths, then scans the result for credential-like material.
    /// </summary>
    /// <remarks>
    ///     Paths are redacted BEFORE the scan. The scanner's keyword-free fallback rejects any 32+ character run
    ///     drawn from <c>[A-Za-z0-9+/=_-]</c> whose entropy is 4.5 or more, and a deep absolute path is one such run
    ///     (measured 4.83), so scanning first made every <c>dotnet build</c> / <c>dotnet test</c> evidence record
    ///     unpersistable. The order does not weaken the policy: a path-shaped match loses its bytes either way, and a
    ///     secret that is not path-shaped still reaches the scan, the Unix pass firing only on an unprefixed '/'.
    /// </remarks>
    private static string SanitizeText(string text, bool allowRedaction, string[] protectedRoots)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Path redaction runs first; this method's remarks record why the secret scan cannot precede it.
        var sanitized = text;
        foreach (var root in protectedRoots.Where(static root => !string.IsNullOrWhiteSpace(root) && root.Length > 1)
                                           .Distinct(StringComparer.OrdinalIgnoreCase)
                                           .OrderByDescending(static root => root.Length))
        {
            sanitized = sanitized.Replace(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                RedactedPath,
                StringComparison.OrdinalIgnoreCase);
        }

        // MatchEvaluator rather than a constant: the replacement keeps layout-free trailing segments. Regex.Replace
        // does not re-scan replacement text, so a marker an earlier pass wrote is never re-matched by a later one.
        sanitized = WindowsAbsolutePathRegex().Replace(sanitized, static match => RedactAbsolutePath(match.Value));
        sanitized = UncAbsolutePathRegex().Replace(sanitized, static match => RedactAbsolutePath(match.Value));
        sanitized = UnixAbsolutePathRegex().Replace(sanitized, static match => RedactAbsolutePath(match.Value));

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
