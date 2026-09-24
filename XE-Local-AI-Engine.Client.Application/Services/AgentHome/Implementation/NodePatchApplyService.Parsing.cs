namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Globalization;
using System.Text;

internal sealed partial class NodePatchApplyService
{
    // The reasons stay path-free prose; the refused entry's own name rides beside them on PatchApplyRejection.Path,
    // which is null whenever the block's path could not be reached safely.
    private const string GitDirectoryRejection = "a patch block targets a git directory.";

    private const string QuotedPathRejection = "a patch block has a quoted path that cannot be read.";

    private const string UnwritableNameRejection = "a patch block has a path holding a character the node will not write.";

    private const string GitlinkRejection = "a patch block changes a submodule reference, which is not supported.";

    private const string SymlinkRejection = "a patch block creates or changes a symbolic link, which is not supported.";

    /// <summary>The git mode of a submodule pointer.</summary>
    private const string GitlinkMode = "160000";

    /// <summary>
    ///     The git mode of a symbolic link. Nothing downstream catches one: the within-root guard validates the
    ///     link's OWN path, never where it points, and the preview would call it an ordinary added file.
    /// </summary>
    private const string SymlinkMode = "120000";

    private static List<string> SplitBlocks(string patchText)
    {
        var blocks = new List<string>();
        var lines = patchText.Split('\n');
        var builder = new StringBuilder();
        var inBlock = false;

        foreach (var line in lines)
        {
            if (line.StartsWith(DiffHeaderPrefix, StringComparison.Ordinal))
            {
                if (inBlock)
                {
                    blocks.Add(builder.ToString());
                    builder.Clear();
                }

                inBlock = true;
            }

            if (inBlock)
            {
                builder.Append(line).Append('\n');
            }
        }

        if (inBlock && builder.Length > 0)
        {
            blocks.Add(builder.ToString());
        }

        return blocks;
    }

    /// <summary>
    ///     Parses a single per-file patch block.
    /// </summary>
    /// <remarks>
    ///     Security contract: the written paths come from the BODY lines (<c>--- a/…</c>, <c>+++ b/…</c>,
    ///     <c>rename from/to</c>, <c>copy from/to</c>) that git acts on, never the <c>diff --git</c> header, which
    ///     serves only as a cross-check that its b-path matches <c>+++ b/</c>. That is what makes the alias,
    ///     traversal and cross-alias guards authoritative independently of git.
    /// </remarks>
    private static ParsedBlock ParseBlock(string block)
    {
        var lines = block.Split('\n');

        var isBinary = block.Contains("GIT binary patch", StringComparison.Ordinal)
                       || lines.Any(line => line.StartsWith("Binary files ", StringComparison.Ordinal));

        // A gitlink is a submodule pointer: `git apply` answers one with an empty directory, so it is refused by
        // name. The index-line arm catches a same-mode pointer bump, which carries no `mode 160000` line at all.
        if (DeclaresMode(lines, GitlinkMode))
        {
            return ParsedBlock.Rejected(GitlinkRejection, TryDescribeTarget(lines));
        }

        // A symlink block's one-line content IS the link target, so applying it creates a REAL link on the host
        // pointing wherever that names. Refused by name for the same reason as the gitlink; see SymlinkMode.
        if (DeclaresMode(lines, SymlinkMode))
        {
            return ParsedBlock.Rejected(SymlinkRejection, TryDescribeTarget(lines));
        }

        // Every path git can act on comes from one of these body-line prefixes, with /dev/null skipped for new and
        // deleted files. They stay constants so raw unified-diff sigils inline do not read as commented-out code.
        const string prefixSource = "---";
        const string prefixDest = "+++";
        var bodyPaths = new List<BodyPath>();
        foreach (var line in lines)
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal)
                && !line.StartsWith("--- /dev/null", StringComparison.Ordinal))
            {
                bodyPaths.Add(new BodyPath(prefixSource, line[4..]));
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal)
                     && !line.StartsWith("+++ /dev/null", StringComparison.Ordinal))
            {
                bodyPaths.Add(new BodyPath(prefixDest, line[4..]));
            }
            else if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                bodyPaths.Add(new BodyPath("rename from", line[12..]));
            }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                bodyPaths.Add(new BodyPath("rename to", line[10..]));
            }
            else if (line.StartsWith("copy from ", StringComparison.Ordinal))
            {
                bodyPaths.Add(new BodyPath("copy from", line[10..]));
            }
            else if (line.StartsWith("copy to ", StringComparison.Ordinal))
            {
                bodyPaths.Add(new BodyPath("copy to", line[8..]));
            }
        }

        if (bodyPaths.Count == 0)
        {
            // A mode-only block has no unified-diff body lines, and git acts on the header path, so that path goes through
            // the same traversal and alias guards as a body path rather than leaving git as the only backstop.
            var headerPath = TryParseHeaderAPath(lines[0]);
            if (headerPath is null)
            {
                return ParsedBlock.Rejected("a mode-only patch block has an unparseable or missing header path.");
            }

            var (headerAlias, headerRelative) = headerPath;
            if (ContainsTraversal(headerRelative))
            {
                return ParsedBlock.Rejected("a patch block targets a path outside its folder.", Describe(headerAlias, headerRelative));
            }

            if (ContainsGitDirectory(headerRelative))
            {
                return ParsedBlock.Rejected(GitDirectoryRejection, Describe(headerAlias, headerRelative));
            }

            // The same refusal the body paths get: this block's header path is the only path it has, so a name the
            // host cannot write must not slip past on this branch either.
            var headerPathText = string.Create(CultureInfo.InvariantCulture, $"{headerAlias}/{headerRelative}");
            if (HasHostInvalidNameCharacter(headerPathText)
                || (GitQuotedPath.IsQuoted(lines[0][DiffHeaderPrefix.Length..]) && HasUnsafeControlCharacter(headerPathText)))
            {
                return ParsedBlock.Rejected(UnwritableNameRejection, Describe(headerAlias, headerRelative));
            }

            return new ParsedBlock
            {
                Alias = headerAlias,
                Text = block,
                IsBinary = isBinary,
                TargetRelativePaths = [headerRelative],
                Files = []
            };
        }

        // Split each body path into alias + relative, then validate traversal and cross-alias references.
        var allAliasResults = new List<BodyAliasPath>();
        foreach (var (prefix, raw) in bodyPaths)
        {
            // Unified-diff body paths carry an "a/" or "b/" diff prefix; rename/copy lines do not.
            var normalized = raw.Trim();

            // git C-quotes a name holding a quote, a backslash or a control byte, REGARDLESS of core.quotePath. It is
            // decoded HERE, before the diff prefix comes off, so every guard below reads the name git will write.
            var wasQuoted = GitQuotedPath.IsQuoted(normalized);
            if (wasQuoted)
            {
                if (GitQuotedPath.TryDecode(normalized) is not { } decoded)
                {
                    return ParsedBlock.Rejected(QuotedPathRejection);
                }

                normalized = decoded;
            }

            if (normalized.StartsWith("a/", StringComparison.Ordinal) || normalized.StartsWith("b/", StringComparison.Ordinal))
            {
                normalized = normalized[2..];
            }

            var split = SplitAlias(normalized);
            if (split is null)
            {
                return ParsedBlock.Rejected("a patch block has a path with no alias segment.");
            }

            var (alias, relative) = split;

            if (ContainsTraversal(relative))
            {
                return ParsedBlock.Rejected("a patch block targets a path outside its folder.", Describe(alias, relative));
            }

            if (ContainsGitDirectory(relative))
            {
                return ParsedBlock.Rejected(GitDirectoryRejection, Describe(alias, relative));
            }

            // A name the host cannot write is refused however the patch spelled it; a decoded one holding more than
            // it was quoted for too. NAMED, unlike a raw literal, because SafeDisplayPath has escaped the text.
            if (HasHostInvalidNameCharacter(normalized) || (wasQuoted && HasUnsafeControlCharacter(normalized)))
            {
                return ParsedBlock.Rejected(UnwritableNameRejection, Describe(alias, relative));
            }

            allAliasResults.Add(new BodyAliasPath
            {
                Prefix = prefix,
                Alias = alias,
                Relative = relative
            });
        }

        // All paths in the block must belong to the same alias (cross-alias rename/copy is a path-escape vector).
        var aliases = allAliasResults.Select(result => result.Alias).Distinct(StringComparer.Ordinal).ToArray();
        if (aliases.Length != 1)
        {
            return ParsedBlock.Rejected("a patch block renames or copies across selected folders.", TryDescribeTarget(lines));
        }

        var blockAlias = aliases[0];

        // Cross-check the header b-path alias against the authoritative body alias: the header can mis-split on a name
        // holding a space, a letter and a slash, so a mismatch with a present body path is a crafted-patch signal.
        var destBodyPath = allAliasResults.FirstOrDefault(result => result.Prefix == prefixDest);
        if (destBodyPath is not null)
        {
            var headerBAlias = TryParseHeaderAPath(lines[0])?.Alias;
            if (headerBAlias is not null && !string.Equals(headerBAlias, blockAlias, StringComparison.Ordinal))
            {
                return ParsedBlock.Rejected("the patch header b-path does not match the body destination path.",
                    Describe(destBodyPath.Alias, destBodyPath.Relative));
            }
        }

        // Collect distinct relative target paths for the within-root guard in BuildAliasPlanAsync.
        var targetPaths = allAliasResults.Select(result => result.Relative).Distinct(StringComparer.Ordinal).ToArray();

        var changeType = DetermineChangeType(block);

        // Display path: destination side, or the source side for a pure delete (no destination body line).
        var bRelative = allAliasResults
                        .Where(result => result.Prefix == prefixDest || result.Prefix is "rename to" or "copy to")
                        .Select(result => result.Relative)
                        .FirstOrDefault();
        var aRelative = allAliasResults
                        .Where(result => result.Prefix == prefixSource || result.Prefix is "rename from" or "copy from")
                        .Select(result => result.Relative)
                        .FirstOrDefault();
        var displayRelative = changeType == "deleted"
            ? aRelative ?? targetPaths[0]
            : bRelative ?? targetPaths[0];

        // The DISPLAY path only. The paths git acts on are TargetRelativePaths, which stay exactly as the patch
        // wrote them: escaping here must never change which patches apply, only what the operator reads.
        var files = new List<PatchApplyFileEntry>
        {
            new()
            {
                Alias = blockAlias,
                RelativePath = SafeDisplayPath(displayRelative),
                ChangeType = changeType
            }
        };

        return new ParsedBlock
        {
            Alias = blockAlias,
            Text = block,
            IsBinary = isBinary,
            TargetRelativePaths = targetPaths,
            Files = files
        };
    }

    /// <summary>
    ///     The folder-relative name to show beside a refusal, or <see langword="null" /> when the block has no path
    ///     that is safe to echo.
    /// </summary>
    /// <remarks>
    ///     Reached before the per-path guards run, so it repeats their entry conditions rather than trusting them: a
    ///     C-quoted path is decoded by the same decoder the guards use and stays unnamed when that refuses, so raw
    ///     undecoded bytes never reach a log line or the dialog.
    /// </remarks>
    private static string? TryDescribeTarget(string[] lines)
    {
        var candidate = FirstBodyPathCandidate(lines);
        if (candidate is null)
        {
            return TryParseHeaderAPath(lines[0]) is { } headerPath ? Describe(headerPath.Alias, headerPath.Relative) : null;
        }

        var normalized = candidate.Trim();
        if (GitQuotedPath.IsQuoted(normalized))
        {
            if (GitQuotedPath.TryDecode(normalized) is not { } decoded)
            {
                return null;
            }

            normalized = decoded;
        }

        if (normalized.StartsWith("a/", StringComparison.Ordinal) || normalized.StartsWith("b/", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return SplitAlias(normalized) is { } split ? Describe(split.Alias, split.Relative) : null;
    }

    /// <summary>The block's destination path if it has one, else its source path. Both raw, still diff-prefixed.</summary>
    private static string? FirstBodyPathCandidate(string[] lines)
    {
        string[] destinationPrefixes = ["+++ ", "rename to ", "copy to "];
        string[] sourcePrefixes = ["--- ", "rename from ", "copy from "];
        return FirstAfterAnyPrefix(lines, destinationPrefixes) ?? FirstAfterAnyPrefix(lines, sourcePrefixes);
    }

    private static string? FirstAfterAnyPrefix(string[] lines, string[] prefixes)
    {
        foreach (var line in lines)
        {
            foreach (var prefix in prefixes)
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal) && line[prefix.Length..] is var rest && rest != "/dev/null")
                {
                    return rest;
                }
            }
        }

        return null;
    }

    /// <summary>Joins an already-split alias and relative into the displayable <c>&lt;alias&gt;/&lt;rel&gt;</c> form.</summary>
    private static string Describe(string alias, string relative)
    {
        return SafeDisplayPath(string.Create(CultureInfo.InvariantCulture, $"{alias}/{relative}"));
    }

    /// <summary>
    ///     Renders a model-authored path so that what the operator reads is what the patch names.
    /// </summary>
    /// <remarks>
    ///     Every code point that can move, hide or reorder the text around it — Cc, Cf (bidi overrides and isolates,
    ///     zero-width marks, the byte-order mark), Zl/Zp and any unpaired surrogate — becomes a visible
    ///     <c>\u{XXXX}</c> escape, so a spoofed name cannot render as the name it imitates. Everything else, an
    ///     umlaut or an ideograph included, passes through: a display rule, not a character set. It decides nothing
    ///     about whether a patch applies, and the paths git acts on come from elsewhere.
    /// </remarks>
    private static string SafeDisplayPath(string path)
    {
        if (!path.Any(character => IsUnsafeToDisplay(character)))
        {
            return path;
        }

        var builder = new StringBuilder(path.Length);
        var index = 0;
        while (index < path.Length)
        {
            var current = path[index];
            if (char.IsHighSurrogate(current) && index + 1 < path.Length && char.IsLowSurrogate(path[index + 1]))
            {
                // A well-formed pair stands for one code point, so the category that decides its fate is the RUNE's,
                // not either half's — every surrogate reads as category Surrogate on its own.
                var rune = new Rune(current, path[index + 1]);
                if (IsUnsafeCategory(Rune.GetUnicodeCategory(rune)))
                {
                    AppendEscaped(builder, rune.Value);
                }
                else
                {
                    _ = builder.Append(current).Append(path[index + 1]);
                }

                index += 2;
                continue;
            }

            if (IsUnsafeToDisplay(current))
            {
                AppendEscaped(builder, current);
            }
            else
            {
                _ = builder.Append(current);
            }

            index++;
        }

        return builder.ToString();
    }

    private static bool IsUnsafeToDisplay(char character)
    {
        return IsUnsafeCategory(CharUnicodeInfo.GetUnicodeCategory(character));
    }

    private static bool IsUnsafeCategory(UnicodeCategory category)
    {
        return category is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.Surrogate;
    }

    private static void AppendEscaped(StringBuilder builder, int codePoint)
    {
        _ = builder.Append("\\u{").Append(codePoint.ToString("X4", CultureInfo.InvariantCulture)).Append('}');
    }

    /// <summary>
    ///     Parses the <c>a/…</c> path from a <c>diff --git a/… b/…</c> header into an <see cref="AliasPath" /> using
    ///     <see cref="SplitAlias" />, or <see langword="null" /> when the header cannot be parsed.
    /// </summary>
    /// <remarks>
    ///     Serves the blocks with no <c>---</c>/<c>+++</c> body lines — a mode change, a binary block, an empty file
    ///     added or deleted — whose header path is the only one they have and goes through the same guards as any
    ///     target path, and the advisory alias cross-check, where an unparseable header is non-fatal. It reads a
    ///     name only where git does: each side is C-quoted on its own, an unquoted header must spell the same name
    ///     twice, and a rename header therefore parses as none — as it does for git, whose body lines rule there.
    /// </remarks>
    private static AliasPath? TryParseHeaderAPath(string header)
    {
        if (!header.StartsWith(DiffHeaderPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var afterPrefix = header[DiffHeaderPrefix.Length..].TrimEnd('\r');
        string aPath;
        if (GitQuotedPath.IsQuoted(afterPrefix))
        {
            // git quotes each side of the header on its own, so the a-path ends at its own closing quote.
            var closing = GitQuotedPath.FindClosingQuote(afterPrefix);
            if (closing < 0 || GitQuotedPath.TryDecode(afterPrefix[..(closing + 1)]) is not { } decoded)
            {
                return null;
            }

            aPath = decoded;
        }
        else
        {
            // git accepts an unquoted header only where the name shows up TWICE in the same form, so the text is
            // "a/" + N + " b/" + N and N's length follows from the line's; the first " b/" would truncate one.
            const string sourcePrefix = "a/";
            const string separator = " b/";
            var nameLength = (afterPrefix.Length - sourcePrefix.Length - separator.Length) / 2;
            if (nameLength <= 0
                || afterPrefix.Length != sourcePrefix.Length + separator.Length + (2 * nameLength)
                || !afterPrefix.AsSpan(sourcePrefix.Length + nameLength, separator.Length).SequenceEqual(separator)
                || !afterPrefix.AsSpan(sourcePrefix.Length + nameLength + separator.Length)
                               .SequenceEqual(afterPrefix.AsSpan(sourcePrefix.Length, nameLength)))
            {
                return null;
            }

            aPath = afterPrefix[..(sourcePrefix.Length + nameLength)];
        }

        // Strip the "a/" prefix: the header pairs an a-path with a b-path, and only the a-side feeds the traversal
        // guard, with SplitAlias extracting the alias from the stripped path.
        return aPath.StartsWith("a/", StringComparison.Ordinal) ? SplitAlias(aPath[2..]) : null;
    }

    private static string DetermineChangeType(string block)
    {
        if (block.Contains("\nrename from ", StringComparison.Ordinal) || block.Contains("\nrename to ", StringComparison.Ordinal))
        {
            return "renamed";
        }

        if (block.Contains("\ncopy from ", StringComparison.Ordinal) || block.Contains("\ncopy to ", StringComparison.Ordinal))
        {
            return "copied";
        }

        if (block.Contains("\nnew file mode ", StringComparison.Ordinal))
        {
            return "added";
        }

        if (block.Contains("\ndeleted file mode ", StringComparison.Ordinal))
        {
            return "deleted";
        }

        return "modified";
    }

    /// <summary>
    ///     Splits a path whose <c>a/</c> or <c>b/</c> diff prefix is already off into its alias and the rest.
    /// </summary>
    /// <remarks>
    ///     Splits on <c>/</c> alone, which is the only separator git puts in a patch path and the only one
    ///     <c>git apply -p2</c> counts, so a decoded name holding a literal backslash stays the ONE name git writes
    ///     rather than being re-cut into two. The traversal and git-directory guards still read a backslash as a
    ///     separator, so a Windows-shaped escape hidden behind one is refused there instead.
    /// </remarks>
    private static AliasPath? SplitAlias(string path)
    {
        var separatorIndex = path.IndexOf(value: '/', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex == path.Length - 1)
        {
            return null;
        }

        var alias = path[..separatorIndex];
        var relative = path[(separatorIndex + 1)..];
        return relative.Length == 0 ? null : new AliasPath(alias, relative);
    }

    /// <summary>
    ///     Whether any segment of a path holds a character this host cannot put in a file name, per
    ///     <see cref="Path.GetInvalidFileNameChars" />.
    /// </summary>
    /// <remarks>
    ///     Asked of EVERY path, quoted or not: git C-quotes for a quote, a backslash or a control byte and nothing
    ///     else, so <c>: &lt; &gt; | ? *</c> — all invalid on Windows — arrive unquoted. A quote and a backslash are
    ///     ordinary bytes on a POSIX host, which keeps those names applyable there; on Windows this refuses them BY
    ///     NAME instead of leaving git to fail mid-apply. It knows neither the reserved device names nor a trailing
    ///     dot or space, which <see cref="Path.GetInvalidFileNameChars" /> does not cover either.
    /// </remarks>
    private static bool HasHostInvalidNameCharacter(string path)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return path.Split('/').Any(segment => segment.IndexOfAny(invalid) >= 0);
    }

    /// <summary>
    ///     Whether a DECODED path holds a character that can move, hide or reorder the text around it.
    /// </summary>
    /// <remarks>
    ///     Asked only of a path the patch C-quoted, because a raw one carrying the same character is a pinned
    ///     contract: it is shown escaped and still applies. Reaching it through an octal escape is not, and a name
    ///     that had to be quoted for a control byte has no business carrying more than the byte it was quoted for.
    /// </remarks>
    private static bool HasUnsafeControlCharacter(string path)
    {
        return path.Any(character => IsUnsafeToDisplay(character));
    }

    /// <summary>
    ///     Whether the block declares <paramref name="mode" /> on any of the four mode lines, or on the
    ///     <c>index &lt;a&gt;..&lt;b&gt; &lt;mode&gt;</c> header — how git states an UNCHANGED mode, which carries no
    ///     mode line at all: a submodule pointer bump, or a retargeted symlink.
    /// </summary>
    /// <remarks>
    ///     Matched as a whole LINE, both ends: every line inside a hunk carries a <c>+</c>, <c>-</c> or space sigil,
    ///     so a file whose CONTENT is the text of a mode line can never be mistaken for one.
    /// </remarks>
    private static bool DeclaresMode(string[] lines, string mode)
    {
        var suffix = " " + mode;
        return lines.Any(line => IsModeLine(line, suffix));
    }

    private static bool IsModeLine(string line, string modeSuffix)
    {
        var trimmed = line.TrimEnd('\r');
        if (!trimmed.EndsWith(modeSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        return trimmed.StartsWith("new file mode ", StringComparison.Ordinal)
               || trimmed.StartsWith("deleted file mode ", StringComparison.Ordinal)
               || trimmed.StartsWith("new mode ", StringComparison.Ordinal)
               || trimmed.StartsWith("old mode ", StringComparison.Ordinal)
               || trimmed.StartsWith("index ", StringComparison.Ordinal);
    }

    private static bool ContainsTraversal(string relativePath)
    {
        var segments = relativePath.Replace(oldChar: '\\', newChar: '/').Split('/');
        return segments.Any(segment => segment is "..");
    }

    /// <summary>
    ///     Whether any path segment IS the git directory — a write there reaches the hooks and filter drivers
    ///     <see cref="AgentHomeGitHardening" /> keeps node-owned. Matched case-insensitively (a case-folding host)
    ///     and by segment equality (<c>.gitattributes</c> is ordinary).
    /// </summary>
    private static bool ContainsGitDirectory(string relativePath)
    {
        var segments = relativePath.Replace(oldChar: '\\', newChar: '/').Split('/');
        return segments.Any(segment => string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase));
    }

    // One raw path line lifted out of a patch block's body, tagged with the unified-diff prefix it came from.
    private sealed record BodyPath(string Prefix, string Raw);

    // A repo path split at its first segment: the selected-folder alias that owns it, and the path within that folder.
    private sealed record AliasPath(string Alias, string Relative);

    // A body path after the split, still carrying the prefix so the destination side can be told from the source side.
    private sealed record BodyAliasPath
    {
        public required string Prefix { get; init; }

        public required string Alias { get; init; }

        public required string Relative { get; init; }
    }

    private sealed record ParsedBlock
    {
        public string Alias { get; init; } = string.Empty;

        public string Text { get; init; } = string.Empty;

        public bool IsBinary { get; init; }

        public IReadOnlyList<string> TargetRelativePaths { get; init; } = [];

        public IReadOnlyList<PatchApplyFileEntry> Files { get; init; } = [];

        public PatchApplyRejection? Rejection { get; init; }

        public static ParsedBlock Rejected(string reason, string? path = null)
        {
            return new ParsedBlock
            {
                Rejection = new PatchApplyRejection
                {
                    Reason = reason,
                    Path = path
                }
            };
        }
    }
}
