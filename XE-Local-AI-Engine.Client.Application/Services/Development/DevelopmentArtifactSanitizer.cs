namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

internal static partial class DevelopmentArtifactSanitizer
{
    private const string RedactedPath = "[REDACTED:development-path]";

    /// <summary>
    ///     How many trailing path segments a GENERIC redaction keeps, and how many leading ones it always destroys.
    ///     <para>
    ///         Measured cost of keeping none: a container running under a read-only root filesystem fails every
    ///         <c>dotnet</c> invocation with <c>mkdir("/tmp/.dotnet/shm/session1", …) == -1; errno == EROFS</c>. The
    ///         generic pass replaced the whole path, leaving a message that says a <c>mkdir</c> failed on a read-only
    ///         filesystem without saying WHICH directory — for a fault whose entire diagnosis is which directory. The
    ///         report was stored, rendered, and useless.
    ///     </para>
    ///     <para>
    ///         Keeping the trailing two segments is deliberately not "keep the path". Host IDENTITY lives in the
    ///         LEADING segments — <c>/home/&lt;user&gt;</c>, <c>/Users/&lt;user&gt;</c>, <c>C:\Users\&lt;user&gt;</c>,
    ///         <c>/mnt/c/Users/&lt;user&gt;</c>, <c>/run/user/&lt;uid&gt;</c> — so at least the first two are always
    ///         replaced, whatever the path's depth, and a path too shallow to have a tail left over after that is
    ///         redacted whole. <c>/etc/passwd</c> and <c>/home/dev</c> therefore still collapse to the bare marker.
    ///     </para>
    ///     <para>
    ///         What survives is a RELATIVE tail, which is the same class of information the targeted pass above
    ///         already preserves for engine roots (see the lookbehind comment) — a tail carries no host layout, only
    ///         the leaf of whatever the command was touching. This is the conservative half of the fix: the fault
    ///         becomes identifiable without the redaction becoming optional.
    ///     </para>
    /// </summary>
    private const int PreservedTailSegments = 2;

    private const int AlwaysRedactedLeadingSegments = 2;

    private static readonly char[] PathSeparators = ['/', '\\'];

    /// <summary>
    ///     Segments whose IMMEDIATE successor names a principal (a user account or a uid), wherever they appear in the
    ///     path. The preserved tail may never begin at or before that successor. This is what makes the redaction depth-
    ///     independent: <c>/home/&lt;user&gt;</c> puts the principal second, <c>/run/user/&lt;uid&gt;</c> third, and WSL's
    ///     <c>/mnt/c/Users/&lt;user&gt;</c> fourth — a purely positional rule preserved the last of those verbatim.
    /// </summary>
    private static readonly HashSet<string> IdentityContainerSegments =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "home",
            "Users",
            "user"
        };

    [GeneratedRegex(@"(?<![A-Za-z0-9])![A-Za-z][A-Za-z0-9]{11,}(?![A-Za-z0-9])", RegexOptions.ExplicitCapture, 2000)]
    private static partial Regex BarePasswordLikeValueRegex();

    // The second lookbehind is what keeps a TARGETED redaction legible, and it is load-bearing rather than cosmetic.
    // Without it the two passes fight: the targeted pass replaces the engine root, leaving
    // "[REDACTED:development-path]/src/Lib/Foo.cs(7,13)", and then this pattern matches the very next '/' — ']' is not
    // in the excluded lookbehind class — and swallows the file and line as well. Measured against real `dotnet build`
    // output, the whole compiler diagnostic collapsed to two adjacent markers, so the targeted pass bought nothing at
    // all and a reviewer could not tell which file failed. Excluding the marker keeps exactly the workspace-relative
    // remainder, which is the part a reviewer needs and the part that carries no host information.
    //
    // There is deliberately no glob exemption here, and there was one for a day. The 2026-09-05 live round found every
    // coder and reviewer prompt carrying "Protected test patterns: **[REDACTED:development-path]/*.cs,
    // **[REDACTED:development-path] ..." - all nine of DevelopmentCommandProfileCatalog.DefaultProtectedPaths
    // swallowed, so the one line of the prompt naming the files the coder was forbidden to touch was the one line the
    // operator could not read. The first fix exempted a '/' after '*', the second one after '**'; both were the same
    // mistake, because a lookbehind exempts a SHAPE, and any absolute path an attacker or a confused model writes in
    // that shape - "**/home/alice/private" - then crosses this boundary intact, which is the boundary
    // DevelopmentCloudContextBuilder crosses to a cloud provider.
    //
    // The rule the sanitizer now holds: it never exempts a shape. Legibility is bought where the safe strings are
    // actually KNOWN instead - SanitizePromptText protects the profile's own protected-path literals by placeholder
    // around these passes - so nothing here has to guess which stars were syntax.
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

        // A POSITIONAL rule alone is not enough, because identity is not always in the first two segments. On this very
        // box (WSL2) `/mnt/c/Users/<user>` puts the username FOURTH, and `/run/user/<uid>` puts the uid THIRD — so the
        // positional rule happily preserved `[REDACTED]/Users/<user>`, leaking exactly what it set out to destroy.
        // Anchor on the CONTAINER instead: whatever segment follows a home-directory container names a principal, so the
        // preserved tail may never begin at or before it, at any depth.
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
    ///     The roots a Development artifact's targeted redaction must cover: the three HOST roots, plus every root the
    ///     same directories are known by INSIDE the sandbox.
    ///     <para>
    ///         The sandbox half is not belt and braces. A command that runs in a container prints container-internal
    ///         paths, which never match a host root — so targeted redaction becomes a silent no-op, the generic pattern
    ///         fires instead, and the whole diagnostic collapses to one undifferentiated marker. Nothing errors; the
    ///         reviewer's view of a failure simply degrades. Under the process provider the two halves are identical and
    ///         the duplicates are discarded, so this is one code path for both providers rather than a container branch.
    ///     </para>
    /// </summary>
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
    ///     Sanitizes an ENGINE-authored prompt. Redacts rather than rejecting, for the same reason
    ///     <see cref="Sanitize(DevelopmentCommandEvidence, string[])" /> does: a coder or reviewer prompt is assembled
    ///     here out of the task title, its requirements, the base commit, the workspace's carried-file list and the
    ///     operator's own instruction — text that legitimately carries absolute paths and long hashes, not model prose
    ///     that has no business containing one. Rejecting on a match would mean the attempts whose prompts are most
    ///     worth recording are exactly the ones that leave no record.
    ///     <para>
    ///         <paramref name="preservedGlobs" /> is the profile's own protected-path pattern list, and each literal in
    ///         it survives verbatim wherever it occurs. The prompt renders those patterns (see
    ///         <see cref="DevelopmentTestWritePolicy.Prompt" />) and the generic Unix pass fired on the '/' inside
    ///         <c>**/*Tests.cs</c>, so on 2026-09-05 the one line telling the coder which files it may not touch was
    ///         the one line the operator could not read. The fix belongs HERE and not in the pattern: this layer knows
    ///         the exact strings that are glob syntax, whereas a lookbehind can only recognise a shape, and every
    ///         absolute path written in that shape would ride the exemption out to a cloud provider.
    ///     </para>
    /// </summary>
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
    ///     A stand-in no pass in <see cref="SanitizeText(string, bool, string[])" /> can match: the delimiters are
    ///     Unicode private-use characters, so the run carries no '/', no drive letter and none of the
    ///     <c>[A-Za-z0-9+/=_-]</c> alphabet the secret scanner's entropy fallback measures. Only the digits between
    ///     them vary, and they cannot merge with neighbouring text because the delimiters bound them.
    /// </summary>
    private static string Placeholder(int index) =>
        $"\uE000{index}\uE001";

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

        // MatchEvaluator rather than a constant: the replacement keeps the path's trailing segments when the path is
        // deep enough for them to be layout-free. Regex.Replace does not re-scan replacement text, so a marker written
        // by an earlier pass is never re-matched by a later one.
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
