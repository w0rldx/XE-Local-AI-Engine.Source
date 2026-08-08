namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Buffers;
using System.Text;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

/// <summary>
///     Derives the managed workspace's whitespace policy from what the repository itself STORES, and writes it into
///     <c>.git/info/attributes</c> before the validation gate's first command runs.
///     <para>
///         <b>The defect.</b> Every .NET validation profile begins with <c>git diff --check HEAD -- .</c>. Git's
///         default whitespace rules count the CR of a CRLF pair as trailing whitespace, so on a repository that
///         legitimately stores CRLF in its blobs — the norm for a Windows-native project — that command reports
///         <c>trailing whitespace</c> on every changed line and exits 2. The gate then fails at command one on a
///         perfectly correct change, and the operator is told their patch has whitespace errors. Reproduced on
///         git 2.53.0: a three-line CRLF file plus one added line exits 2 with the default rules and 0 with
///         <c>cr-at-eol</c>.
///     </para>
///     <para>
///         <b>Why not just set <c>core.whitespace=cr-at-eol</c>.</b> Because it is a repository-wide switch and the
///         answer is not repository-wide. Measured in the same run: on an LF repository where a change introduces one
///         CRLF line — a genuine defect the check exists to catch — <c>cr-at-eol</c> silences it too. Deleting the
///         whitespace command and setting the option globally are the same mistake in two spellings: both trade a
///         false failure on one kind of repository for a missed failure on the other.
///     </para>
///     <para>
///         <b>Why per PATH.</b> Whole-repository classification is not good enough either, because mixed repositories
///         are the common case rather than the exotic one — this engine's own repository stores 4243 files as LF and
///         exactly one as CRLF. Git's <c>whitespace</c> ATTRIBUTE is per path, so the policy is expressed the way the
///         data actually varies: <c>cr-at-eol</c> is granted to the paths whose INDEX content is CRLF and to nothing
///         else. Every other path keeps the full default rule set, CR included.
///     </para>
///     <para>
///         <b>Why the index and not the worktree.</b> <c>git ls-files --eol</c> reports both, and only <c>i/</c> is the
///         right signal: with <c>core.autocrlf=true</c> — which Git for Windows' system config commonly sets — the
///         worktree is CRLF while the blob is LF, and <c>diff --check</c> compares against the blob. Sampling the
///         worktree would hand <c>cr-at-eol</c> to an ordinary LF repository on every Windows box.
///     </para>
///     <para>
///         <b>Why <c>.git/info/attributes</c> and not the profile's argument vector.</b> The profile is snapshotted
///         into the database and re-derived from the code-owned catalog to prove it has not changed underneath the
///         operator's confirmation, so a per-repository argument cannot live there without a catalog version bump that
///         would invalidate every stored profile. Attributes carry per-path policy natively, the file is engine-written
///         rather than agent-written, and <c>$GIT_DIR/info/attributes</c> outranks an in-tree <c>.gitattributes</c> —
///         so a hostile repository cannot revoke the policy, and it cannot grant itself one either, because this file
///         is rewritten from the index on every workspace preparation.
///     </para>
///     <para>
///         <b>The repository's own config is NOT the source, and could not be.</b> The managed workspace is a
///         standalone clone, and <c>git clone</c> copies no <c>core.*</c> from the source repository — verified: a
///         source repository with <c>core.whitespace=cr-at-eol</c> and <c>core.autocrlf=input</c> produces a clone
///         whose config carries neither. So <see cref="DevelopmentWorkspaceGitConfig.RestoreMinimal" />'s allow-list is
///         not what removes them; they were never there. The workspace is also deliberately more deterministic than the
///         operator's checkout — commands run with <c>HOME</c> pointed at a per-task runtime directory, so no user
///         <c>~/.gitconfig</c> applies. Both are intended, and together they are exactly why the engine has to DERIVE
///         this policy from the repository's content instead of inheriting a setting.
///     </para>
/// </summary>
internal static class DevelopmentWorkspaceWhitespacePolicy
{
    /// <summary>
    ///     The value granted to a CRLF-stored path: CR at end of line is not a whitespace error. Everything else in
    ///     Git's default rule set — other trailing whitespace, a space before a tab, a blank line at end of file —
    ///     still applies, including to these paths.
    /// </summary>
    private const string CrAtEol = "whitespace=cr-at-eol";

    private const string Header = "# Written by the engine from `git ls-files --eol`; see DevelopmentWorkspaceWhitespacePolicy.\n";

    /// <summary>
    ///     Above this many CRLF-stored paths the file names <c>*</c> instead of every path.
    ///     <para>
    ///         Not a memory bound — a bound on Git's own attribute matching, which walks the pattern list for every
    ///         path it looks up, so an exhaustive list on a large all-CRLF repository is quadratic work on every diff.
    ///         A repository with this many CRLF blobs is a CRLF repository, so the wildcard says the same thing the
    ///         list would.
    ///     </para>
    /// </summary>
    public const int MaxExplicitPaths = 2048;

    private static readonly SearchValues<char> GlobMetacharacters = SearchValues.Create("\\\"*?[]");

    /// <summary>
    ///     Reads the workspace's index line endings and writes (or removes) <c>.git/info/attributes</c> accordingly. A
    ///     no-op when the workspace has no Git directory.
    ///     <para>
    ///         A failed <c>ls-files</c> writes nothing, leaving the strict default in place. That is the conservative
    ///         direction and it is not a silent one: git being unusable in the workspace fails the very next command of
    ///         the gate, with git's own message.
    ///     </para>
    /// </summary>
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
                                  cancellationToken)
                              .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return;
        }

        Write(gitDirectory, Render(result.StandardOutput));
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
            // and an escape in the other. Rather than emit a pattern that might name the wrong file, widen to the
            // whole repository — the safe direction here is the one that cannot fail a correct change.
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
    ///     <para>
    ///         The leading <c>/</c> is load-bearing: a pattern with no slash is a glob matched at ANY depth, so a bare
    ///         <c>a.txt</c> would grant the policy to every <c>a.txt</c> in the tree rather than to the one file that
    ///         earned it. It also puts a non-special character first, so a path beginning <c>#</c> or <c>!</c> cannot be
    ///         read as a comment or a negation.
    ///     </para>
    /// </summary>
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

        // A pattern containing whitespace has to be C-quoted, which is safe here precisely because the metacharacter
        // check above already excluded the backslash and the double quote that C-quoting would otherwise have to
        // escape.
        return path.Any(char.IsWhiteSpace) ? "\"/" + path + "\"" : "/" + path;
    }

    /// <summary>
    ///     Replaces the attributes file, or removes it when the repository has nothing to grant.
    ///     <para>
    ///         Deleted rather than overwritten in place, and rewritten on every preparation, for the same two reasons
    ///         <see cref="DevelopmentWorkspaceGitConfig.RestoreMinimal" /> does it: a command from a previous attempt
    ///         can have replaced the file with a symbolic link, and an ordinary write would then follow it out of the
    ///         workspace; and a file left over from a previous attempt is a policy nobody derived from the current
    ///         index.
    ///     </para>
    /// </summary>
    private static void Write(string gitDirectory, string? body)
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
        File.WriteAllText(attributesPath, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
