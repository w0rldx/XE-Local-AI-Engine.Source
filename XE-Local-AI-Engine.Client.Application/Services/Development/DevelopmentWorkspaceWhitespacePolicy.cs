namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Buffers;
using System.Text;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

/// <summary>
///     Derives the managed workspace's whitespace policy from what the repository itself STORES, and writes it into
///     <c>.git/info/attributes</c> before the validation gate's first command runs.
/// </summary>
/// <remarks>
///     Git's default rules count the CR of a CRLF pair as trailing whitespace, so <c>git diff --check</c> fails the
///     gate at command one on a repository that legitimately stores CRLF. <c>cr-at-eol</c> is therefore granted per
///     path, to the paths whose index content is CRLF and to nothing else, because a repository-wide switch would
///     silence the genuine defect on an LF repository. The measurements and the reason a clone cannot inherit this
///     are in <c>docs/wiki/12-security-and-privacy.md</c> ("The whitespace policy is derived from the index").
/// </remarks>
internal static class DevelopmentWorkspaceWhitespacePolicy
{
    /// <summary>The value granted to a CRLF-stored path: CR at end of line is not a whitespace error.</summary>
    /// <remarks>
    ///     Everything else in Git's default rule set — other trailing whitespace, a space before a tab, a blank line
    ///     at end of file — still applies, these paths included.
    /// </remarks>
    private const string CrAtEol = "whitespace=cr-at-eol";

    private const string Header = "# Written by the engine from `git ls-files --eol`; see DevelopmentWorkspaceWhitespacePolicy.\n";

    /// <summary>Above this many CRLF-stored paths the file names <c>*</c> instead of every path.</summary>
    /// <remarks>
    ///     Not a memory bound but a bound on Git's own attribute matching, which walks the pattern list for every
    ///     path it looks up, so an exhaustive list on a large all-CRLF repository is quadratic work on every diff. A
    ///     repository with this many CRLF blobs is a CRLF repository, so the wildcard says what the list would.
    /// </remarks>
    public const int MaxExplicitPaths = 2048;

    private static readonly SearchValues<char> GlobMetacharacters = SearchValues.Create("\\\"*?[]");

    /// <summary>
    ///     Reads the workspace's index line endings and writes (or removes) <c>.git/info/attributes</c> accordingly;
    ///     a no-op when the workspace has no Git directory.
    /// </summary>
    /// <remarks>
    ///     A failed <c>ls-files</c> writes nothing, leaving the strict default in place. That is the conservative
    ///     direction and not a silent one: git being unusable in the workspace fails the gate's very next command,
    ///     with git's own message.
    /// </remarks>
    public static async Task ApplyAsync(HostGitRunner git, string workspacePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var gitDirectory = Path.Combine(workspacePath, ".git");
        if (!Directory.Exists(gitDirectory))
        {
            return;
        }

        var result = await git.RunAsync(workspacePath,
                                  AgentHomeGit.Arguments("ls-files", "--eol", "--", "."),
                                  cancellationToken);
        if (result.ExitCode != 0)
        {
            return;
        }

        await WriteAsync(gitDirectory, Render(result.StandardOutput), cancellationToken);
    }

    /// <summary>
    ///     Turns <c>git ls-files --eol</c> output into the attributes file body, or <see langword="null" /> when the
    ///     repository stores nothing as CRLF and the strict default should stand. Pure, so both directions are provable
    ///     without a repository.
    /// </summary>
    public static string? Render(string lsFilesEolOutput)
    {
        ArgumentNullException.ThrowIfNull(lsFilesEolOutput);

        var paths = new SortedSet<string>(StringComparer.Ordinal);
        var wildcard = false;

        foreach (var rawLine in lsFilesEolOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!line.StartsWith("i/", StringComparison.Ordinal))
            {
                continue;
            }

            // "i/<eol><spaces>w/<eol><spaces>attr/<attr><padding>\t<path>" — the TAB before the path is the only one
            // git emits, so it is the separator regardless of how the columns are padded.
            var separator = line.IndexOf('\t', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            // Split on either separator: the columns are space-padded today and the path is tab-separated, but reading
            // the eol token as everything-up-to-the-first-whitespace does not depend on which one git chose.
            var indexEol = line[2..].Split([' ', '\t'], 2)[0].Trim();
            if (!string.Equals(indexEol, "crlf", StringComparison.Ordinal) && !string.Equals(indexEol, "mixed", StringComparison.Ordinal))
            {
                continue;
            }

            var path = line[(separator + 1)..];
            if (TryRenderPattern(path) is { } pattern)
            {
                _ = paths.Add(pattern);
                continue;
            }

            // A path git itself had to quote, or one carrying a character that is a glob metacharacter in one syntax
            // and an escape in the other: widen to the whole repository rather than emit a pattern naming another file.
            wildcard = true;
        }

        if (paths.Count == 0 && !wildcard)
        {
            return null;
        }

        if (wildcard || paths.Count > MaxExplicitPaths)
        {
            return Header + "* " + CrAtEol + "\n";
        }

        var builder = new StringBuilder(Header);
        foreach (var pattern in paths)
        {
            _ = builder.Append(pattern).Append(' ').Append(CrAtEol).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Renders one repository-relative path as an attributes pattern anchored at the repository root, or
    ///     <see langword="null" /> when it cannot be expressed unambiguously.
    /// </summary>
    /// <remarks>
    ///     The leading <c>/</c> is load-bearing: a pattern with no slash is a glob matched at any depth, so a bare
    ///     <c>a.txt</c> would grant the policy to every <c>a.txt</c> in the tree rather than the one file that earned
    ///     it. It also puts a non-special character first, so a path beginning <c>#</c> or <c>!</c> cannot be read as
    ///     a comment or a negation.
    /// </remarks>
    private static string? TryRenderPattern(string path)
    {
        if (path.Length == 0 || path[0] == '"' || path.Any(char.IsControl))
        {
            return null;
        }

        if (path.AsSpan().IndexOfAny(GlobMetacharacters) >= 0)
        {
            return null;
        }

        // A pattern containing whitespace has to be C-quoted, which is safe precisely because the metacharacter check
        // above already excluded the backslash and the double quote that C-quoting would otherwise have to escape.
        return path.Any(char.IsWhiteSpace) ? "\"/" + path + "\"" : "/" + path;
    }

    /// <summary>Replaces the attributes file, or removes it when the repository has nothing to grant.</summary>
    /// <remarks>
    ///     Deleted rather than overwritten in place, and rewritten on every preparation, for the two reasons
    ///     <see cref="DevelopmentWorkspaceGitConfig.RestoreMinimalAsync" /> does it: a command from a previous attempt
    ///     can have replaced the file with a symbolic link an ordinary write would follow out of the workspace, and a
    ///     leftover file is a policy nobody derived from the current index.
    /// </remarks>
    private static async Task WriteAsync(string gitDirectory, string? body, CancellationToken cancellationToken)
    {
        var infoDirectory = Path.Combine(gitDirectory, "info");
        var attributesPath = Path.Combine(infoDirectory, "attributes");

        var existing = new FileInfo(attributesPath);
        if (existing.Exists || existing.LinkTarget is not null)
        {
            File.Delete(attributesPath);
        }

        if (body is null)
        {
            return;
        }

        _ = Directory.CreateDirectory(infoDirectory);
        await File.WriteAllTextAsync(attributesPath, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
    }
}
