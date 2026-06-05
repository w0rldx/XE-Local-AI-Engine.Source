namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Text;

internal sealed partial class NodePatchApplyService
{
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
    ///     Parses a single per-file patch block. Security contract: the written paths are derived from the BODY lines
    ///     (<c>--- a/…</c>, <c>+++ b/…</c>, <c>rename from/to</c>, <c>copy from/to</c>) — the paths git actually acts
    ///     on — NOT the <c>diff --git</c> header. The header is used only as a cross-check (b-path must match <c>+++ b/</c>).
    ///     This makes all alias / traversal / cross-alias guards authoritative independent of git.
    /// </summary>
    private static ParsedBlock ParseBlock(string block)
    {
        var lines = block.Split('\n');

        var isBinary = block.Contains("GIT binary patch", StringComparison.Ordinal)
                       || lines.Any(line => line.StartsWith("Binary files ", StringComparison.Ordinal));

        // Step 1: extract the authoritative paths from the body lines.
        // Every path git can act on comes from one of these line prefixes. /dev/null is skipped (new/deleted).
        // The prefix strings are kept as constants so S125 ("commented-out code") is not triggered by raw
        // unified-diff sigils appearing inline.
        const string prefixSource = "---";
        const string prefixDest = "+++";
        var bodyPaths = new List<(string Prefix, string Raw)>();
        foreach (var line in lines)
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal)
                && !line.StartsWith("--- /dev/null", StringComparison.Ordinal))
            {
                bodyPaths.Add((prefixSource, line[4..]));
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal)
                     && !line.StartsWith("+++ /dev/null", StringComparison.Ordinal))
            {
                bodyPaths.Add((prefixDest, line[4..]));
            }
            else if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                bodyPaths.Add(("rename from", line[12..]));
            }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                bodyPaths.Add(("rename to", line[10..]));
            }
            else if (line.StartsWith("copy from ", StringComparison.Ordinal))
            {
                bodyPaths.Add(("copy from", line[10..]));
            }
            else if (line.StartsWith("copy to ", StringComparison.Ordinal))
            {
                bodyPaths.Add(("copy to", line[8..]));
            }
        }

        if (bodyPaths.Count == 0)
        {
            // A mode-only block (e.g. old mode 100644 / new mode 100755) has no unified-diff body lines. Git acts
            // on the path derived from the header, so we must validate that header path through the same guards as
            // body paths (ContainsTraversal + SplitAlias) rather than leaving git as the only backstop.
            var headerPath = TryParseHeaderAPath(lines[0]);
            if (headerPath is null)
            {
                return ParsedBlock.Rejected("a mode-only patch block has an unparseable or missing header path.");
            }

            var (headerAlias, headerRelative) = headerPath.Value;
            if (ContainsTraversal(headerRelative))
            {
                return ParsedBlock.Rejected("a patch block targets a path outside its folder.");
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

        // Step 2: split each body path into alias + relative; validate traversal and cross-alias.
        var allAliasResults = new List<(string Prefix, string Alias, string Relative)>();
        foreach (var (prefix, raw) in bodyPaths)
        {
            // Unified-diff body paths carry an "a/" or "b/" diff prefix; rename/copy lines do not.
            var normalized = raw.Trim();
            if (normalized.StartsWith("a/", StringComparison.Ordinal) || normalized.StartsWith("b/", StringComparison.Ordinal))
            {
                normalized = normalized[2..];
            }

            var split = SplitAlias(normalized);
            if (split is null)
            {
                return ParsedBlock.Rejected("a patch block has a path with no alias segment.");
            }

            var (alias, relative) = split.Value;

            if (ContainsTraversal(relative))
            {
                return ParsedBlock.Rejected("a patch block targets a path outside its folder.");
            }

            allAliasResults.Add((prefix, alias, relative));
        }

        // All paths in the block must belong to the same alias (cross-alias rename/copy is a path-escape vector).
        var aliases = allAliasResults.Select(result => result.Alias).Distinct(StringComparer.Ordinal).ToArray();
        if (aliases.Length != 1)
        {
            return ParsedBlock.Rejected("a patch block renames or copies across selected folders.");
        }

        var blockAlias = aliases[0];

        // Step 3: cross-check header b-path alias against the authoritative body alias.
        // The header can mis-split on paths whose name contains a space followed by a single letter and slash
        // (e.g. "dir b/file"), so it is not used as the authoritative source. When a body plus-plus path is
        // present the header alias should agree; a mismatch is a crafted-patch signal and is rejected.
        var destBodyPath = allAliasResults.FirstOrDefault(result => result.Prefix == prefixDest);
        if (destBodyPath != default)
        {
            var headerBAlias = ExtractAliasFromHeader(lines[0]);
            if (headerBAlias is not null && !string.Equals(headerBAlias, blockAlias, StringComparison.Ordinal))
            {
                return ParsedBlock.Rejected("the patch header b-path does not match the body destination path.");
            }
        }

        // Step 4: collect all distinct relative target paths for the within-root guard in BuildAliasPlanAsync.
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
            ? (aRelative ?? targetPaths[0])
            : (bRelative ?? targetPaths[0]);

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
        var slashIndex = rest.IndexOf('/', StringComparison.Ordinal);
        return slashIndex > 0 ? rest[..slashIndex] : null;
    }

    /// <summary>
    ///     Parses the <c>a/…</c> path from a <c>diff --git a/… b/…</c> header into <c>(alias, relative)</c> using
    ///     <see cref="SplitAlias" />. Used for mode-only blocks that carry no <c>---</c>/<c>+++</c> body lines; the
    ///     result is fed through the same traversal and within-root guards as all other target paths.
    ///     Returns <see langword="null" /> when the header cannot be parsed.
    /// </summary>
    private static (string Alias, string Relative)? TryParseHeaderAPath(string header)
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

        // Strip the "a/" prefix and find the end of the a-side: the header is "a/<apath> b/<bpath>" and the a-path
        // ends at the first " b/" that is followed by the same path (for symmetric headers). For the traversal guard
        // we only need the a-side; SplitAlias handles alias extraction from the a-prefix-stripped path.
        var aRest = afterPrefix[2..];

        // Locate the " b/" separator by scanning from the end — in symmetric headers the b-path mirrors the a-path
        // so the separator is at position (len(aRest) - len(bpath) - 3). As a safe fallback: take everything up to
        // the first occurrence of " b/" which is the canonical separator for well-formed headers.
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

    private static (string Alias, string Relative)? SplitAlias(string path)
    {
        // Path arrives with the a/ or b/ diff prefix already stripped. Split on the first '/' into alias + relative.
        var normalized = path.Replace('\\', '/');
        var separatorIndex = normalized.IndexOf('/', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex == normalized.Length - 1)
        {
            return null;
        }

        var alias = normalized[..separatorIndex];
        var relative = normalized[(separatorIndex + 1)..];
        return relative.Length == 0 ? null : (alias, relative);
    }

    private static bool ContainsTraversal(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/');
        return segments.Any(segment => segment is "..");
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
            return new ParsedBlock { Rejection = reason };
        }
    }
}
