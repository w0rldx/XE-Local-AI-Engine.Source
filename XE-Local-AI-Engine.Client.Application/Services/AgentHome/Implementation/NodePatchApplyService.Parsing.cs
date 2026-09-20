namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Text;

internal sealed partial class NodePatchApplyService
{
    // None of these carries a path, in keeping with every other rejection string here.
    private const string GitDirectoryRejection = "a patch block targets a git directory.";

    private const string QuotedPathRejection = "a patch block has a quoted path, which is not supported.";

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
            return ParsedBlock.Rejected(GitlinkRejection);
        }

        // A symlink block's one-line content IS the link target, so applying it creates a REAL link on the host
        // pointing wherever that names. Refused by name for the same reason as the gitlink; see SymlinkMode.
        if (DeclaresMode(lines, SymlinkMode))
        {
            return ParsedBlock.Rejected(SymlinkRejection);
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
                return ParsedBlock.Rejected("a patch block targets a path outside its folder.");
            }

            if (ContainsGitDirectory(headerRelative))
            {
                return ParsedBlock.Rejected(GitDirectoryRejection);
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

            // git C-quotes a name containing a quote, a backslash or a control character, REGARDLESS of
            // core.quotePath. This parser does not unescape, so it refuses by name rather than split a nonsense alias.
            if (normalized.StartsWith('"'))
            {
                return ParsedBlock.Rejected(QuotedPathRejection);
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
                return ParsedBlock.Rejected("a patch block targets a path outside its folder.");
            }

            if (ContainsGitDirectory(relative))
            {
                return ParsedBlock.Rejected(GitDirectoryRejection);
            }

            allAliasResults.Add(new BodyAliasPath { Prefix = prefix, Alias = alias, Relative = relative });
        }

        // All paths in the block must belong to the same alias (cross-alias rename/copy is a path-escape vector).
        var aliases = allAliasResults.Select(result => result.Alias).Distinct(StringComparer.Ordinal).ToArray();
        if (aliases.Length != 1)
        {
            return ParsedBlock.Rejected("a patch block renames or copies across selected folders.");
        }

        var blockAlias = aliases[0];

        // Cross-check the header b-path alias against the authoritative body alias: the header can mis-split on a name
        // holding a space, a letter and a slash, so a mismatch with a present body path is a crafted-patch signal.
        var destBodyPath = allAliasResults.FirstOrDefault(result => result.Prefix == prefixDest);
        if (destBodyPath is not null)
        {
            var headerBAlias = ExtractAliasFromHeader(lines[0]);
            if (headerBAlias is not null && !string.Equals(headerBAlias, blockAlias, StringComparison.Ordinal))
            {
                return ParsedBlock.Rejected("the patch header b-path does not match the body destination path.");
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

        var files = new List<PatchApplyFileEntry>
        {
            new()
            {
                Alias = blockAlias,
                RelativePath = displayRelative,
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
    ///     Extracts the alias component from the <c>diff --git a/…</c> header for the cross-check only.
    ///     Returns <see langword="null" /> when the header cannot be parsed (non-fatal — the guard is advisory).
    /// </summary>
    private static string? ExtractAliasFromHeader(string header)
    {
        if (!header.StartsWith(DiffHeaderPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        // The header is "diff --git a/<rest> b/<rest>". We only need the alias from the a/ side, which is the first
        // path component after "a/". A mis-split here is safe because this is advisory only — advisory-path guard guards.
        var afterPrefix = header[DiffHeaderPrefix.Length..];
        if (!afterPrefix.StartsWith("a/", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = afterPrefix[2..];
        var slashIndex = rest.IndexOf(value: '/', StringComparison.Ordinal);
        return slashIndex > 0 ? rest[..slashIndex] : null;
    }

    /// <summary>
    ///     Parses the <c>a/…</c> path from a <c>diff --git a/… b/…</c> header into an <see cref="AliasPath" /> using
    ///     <see cref="SplitAlias" />, or <see langword="null" /> when the header cannot be parsed.
    /// </summary>
    /// <remarks>
    ///     Used for mode-only blocks that carry no <c>---</c>/<c>+++</c> body lines. The result is fed through the
    ///     same traversal and within-root guards as every other target path.
    /// </remarks>
    private static AliasPath? TryParseHeaderAPath(string header)
    {
        if (!header.StartsWith(DiffHeaderPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var afterPrefix = header[DiffHeaderPrefix.Length..];
        if (!afterPrefix.StartsWith("a/", StringComparison.Ordinal))
        {
            return null;
        }

        // Strip the "a/" prefix: the header pairs an a-path with a b-path, and only the a-side feeds the traversal
        // guard, with SplitAlias extracting the alias from the stripped path.
        var aRest = afterPrefix[2..];

        // The canonical separator for a well-formed header is the first " b/", and a symmetric header mirrors the a-path
        // after it, so everything up to that point is the a-side.
        var sepIndex = aRest.IndexOf(" b/", StringComparison.Ordinal);
        var aPath = sepIndex > 0 ? aRest[..sepIndex] : aRest;

        return SplitAlias(aPath);
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

    private static AliasPath? SplitAlias(string path)
    {
        // Path arrives with the a/ or b/ diff prefix already stripped. Split on the first '/' into alias + relative.
        var normalized = path.Replace(oldChar: '\\', newChar: '/');
        var separatorIndex = normalized.IndexOf(value: '/', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex == normalized.Length - 1)
        {
            return null;
        }

        var alias = normalized[..separatorIndex];
        var relative = normalized[(separatorIndex + 1)..];
        return relative.Length == 0 ? null : new AliasPath(alias, relative);
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

        public string? Rejection { get; init; }

        public static ParsedBlock Rejected(string reason)
        {
            return new ParsedBlock
            {
                Rejection = reason
            };
        }
    }
}
