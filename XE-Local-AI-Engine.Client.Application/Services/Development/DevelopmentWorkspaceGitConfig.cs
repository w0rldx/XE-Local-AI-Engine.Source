namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Globalization;
using System.Text;

/// <summary>
///     Rewrites the managed workspace's <c>.git/config</c> to a known-good minimal file immediately before the engine
///     runs host-side Git against that workspace.
///     <para>
///         <b>Why this exists.</b> A repository-local <c>.git/config</c> can make Git execute arbitrary commands on the
///         machine that runs it — <c>core.fsmonitor</c> on any index refresh, and a <c>filter.&lt;driver&gt;.clean</c>
///         selected by an in-tree <c>.gitattributes</c> on <c>git add</c>. The engine runs <c>reset</c> and
///         <c>add -A</c> on the HOST against this workspace, so those are host-side execution, not sandbox-side.
///         <c>AgentHomeGit</c>'s <c>-c</c> pins close <c>core.fsmonitor</c> and outrank every include chain, but they
///         cannot close <c>filter.*.clean</c>: driver names are arbitrary, so there is no finite set of keys to pin, and
///         Git has no flag that disables attribute processing.
///     </para>
///     <para>
///         <b>Why rewriting works where pinning cannot.</b> A filter driver has to be <em>defined in config</em> to run.
///         An in-tree <c>.gitattributes</c> naming an undefined driver is a no-op. So removing every definition closes
///         <c>filter.*.clean</c>, <c>core.fsmonitor</c> and any future exec-bearing key at once, without enumerating key
///         names — which is the same property the read-only <c>.git/config</c> bind mount gets on the container side.
///         This half is provider-independent, and that matters: the standalone clone (S3.4) turned
///         <c>&lt;workspace&gt;/.git/config</c> into a real, agent-writable file inside the jail, on the process
///         provider that Development actually runs on today.
///     </para>
///     <para>
///         <b>Why minimal is not empty.</b> A clone of a repository using a newer format carries
///         <c>extensions.*</c> keys that Git <em>refuses to operate without</em>, and
///         <c>core.repositoryformatversion</c> is what selects that rule. Truncating the file would turn a security fix
///         into an outage on exactly the repositories most likely to matter. <c>core.filemode</c> and <c>core.bare</c>
///         are preserved for the same reason in miniature: they describe the repository, and a wrong value changes what
///         a diff says.
///     </para>
///     <para>
///         <b>What is deliberately not preserved.</b> <c>origin</c>: the clone drops it on purpose (it points straight
///         back at the trusted source repository), and a test asserts the workspace has no remote — so restoring it here
///         would quietly undo D8. And <c>extensions.worktreeConfig</c>, because it makes Git read a
///         <em>second</em> config file (<c>.git/config.worktree</c>) that this rewrite does not cover; that file is
///         removed alongside rather than sanitised, since a standalone clone has no linked worktrees to need it.
///     </para>
///     <para>
///         <b>This allow-list is NOT what drops the repository's <c>core.whitespace</c> / <c>core.autocrlf</c>.</b> The
///         question comes up because the managed workspace behaves differently from the operator's own checkout, and
///         this file looks like the cause. It is not: the workspace is a standalone CLONE, and <c>git clone</c> copies
///         no <c>core.*</c> from the source repository — verified, a source repository carrying both keys produces a
///         clone whose config carries neither — so those keys were never here to preserve. The workspace is
///         additionally more deterministic than the host checkout on purpose, because commands run with <c>HOME</c>
///         pointed at a per-task runtime directory and therefore see no user <c>~/.gitconfig</c>. Both are intended.
///         The consequence that mattered — the validation gate's first command failing on a repository that
///         legitimately stores CRLF — is answered by DERIVING the policy from the repository's own index rather than by
///         inheriting a setting; see <see cref="DevelopmentWorkspaceWhitespacePolicy" />. Do not "fix" it by adding
///         <c>whitespace</c> or <c>autocrlf</c> to <see cref="PreservedCoreKeys" />: there is nothing to preserve, and
///         a key an agent-writable file supplies is exactly what this allow-list exists to refuse.
///     </para>
///     <para>
///         <b>TOCTOU.</b> There is no meaningful window: evidence export runs after the attempt has finished, with no
///         agent command in flight, and workspace preparation runs before any command has started.
///     </para>
/// </summary>
internal static class DevelopmentWorkspaceGitConfig
{
    private const string CoreSection = "core";
    private const string ExtensionsSection = "extensions";

    /// <summary>
    ///     The <c>core</c> keys that describe the repository rather than instructing Git to run something. Everything
    ///     else in <c>core</c> is dropped, which is what makes this an allow-list rather than a block-list: a key nobody
    ///     has thought of yet is dropped by default instead of surviving until someone remembers to name it.
    /// </summary>
    private static readonly string[] PreservedCoreKeys = ["repositoryformatversion", "filemode", "bare", "symlinks", "ignorecase"];

    /// <summary>
    ///     Removes every configured value from the workspace's <c>.git/config</c> except the repository-describing keys
    ///     Git needs to open the repository at all. A no-op when the workspace has no Git directory.
    /// </summary>
    /// <exception cref="DevelopmentWorkspaceSecurityException">
    ///     The workspace's <c>.git</c> is a symbolic link, which means the path the engine is about to rewrite is not
    ///     the one it created.
    /// </exception>
    public static void RestoreMinimal(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var gitDirectory = Path.Combine(workspacePath, ".git");
        if (!Directory.Exists(gitDirectory))
        {
            // Either the workspace has not been materialised yet, or its .git is the pointer FILE a linked worktree
            // gets — in which case repository-local config lives elsewhere and there is nothing here to rewrite. The
            // standalone-clone assertion in DevelopmentWorkspaceProvider is what rejects the second case; this is not
            // the place to duplicate it.
            return;
        }

        if (new DirectoryInfo(gitDirectory).LinkTarget is not null)
        {
            throw new DevelopmentWorkspaceSecurityException("The managed Development workspace Git directory is a symbolic link.");
        }

        var configPath = Path.Combine(gitDirectory, "config");
        var config = new FileInfo(configPath);

        // A DANGLING symlink reads as "does not exist" through File.Exists, so the entries are read only from a real
        // file — a link's target is not this repository's configuration and must not be carried forward.
        var preserved = config.Exists && config.LinkTarget is null ? ReadPreservedEntries(configPath) : [];

        // Deleted rather than overwritten in place: a command inside the workspace can replace the file with a symlink,
        // and an ordinary write would then follow it out of the workspace. Deleting the link removes the redirection
        // before anything is written through it.
        DeleteIfPresent(configPath);
        DeleteIfPresent(Path.Combine(gitDirectory, "config.worktree"));

        File.WriteAllText(configPath, Render(preserved), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    ///     Removes a path that exists, including a DANGLING symbolic link — which <see cref="File.Exists" /> reports as
    ///     absent while a later write would still follow it. <see cref="FileSystemInfo.LinkTarget" /> is what
    ///     distinguishes the two; <see cref="File.ResolveLinkTarget(string, bool)" /> cannot be used here because it
    ///     throws for a path that is not there at all.
    /// </summary>
    private static void DeleteIfPresent(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists || info.LinkTarget is not null)
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     Parses the existing config far enough to keep the preserved keys, and no further.
    ///     <para>
    ///         Deliberately naive about Git's odder syntax (backslash line continuation, quoted values spanning a
    ///         newline). It can afford to be, because the allow-list makes every parse error fail in the safe direction:
    ///         a continuation line misread as a section header can only cause a key to be <em>dropped</em>, never an
    ///         exec-bearing key to be kept — there is no key outside the allow-list that any misreading can admit.
    ///     </para>
    /// </summary>
    private static List<PreservedEntry> ReadPreservedEntries(string configPath)
    {
        var entries = new List<PreservedEntry>();
        var section = string.Empty;
        var subsection = (string?)null;

        foreach (var rawLine in File.ReadLines(configPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                var close = line.IndexOf(']', StringComparison.Ordinal);
                if (close < 0)
                {
                    continue;
                }

                ParseSectionHeader(line[1..close], out section, out subsection);
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (ResolvePreservedSection(section, key) is { } canonicalSection)
            {
                entries.Add(new PreservedEntry(canonicalSection, subsection, key, value));
            }
        }

        return entries;
    }

    private static void ParseSectionHeader(string header, out string section, out string? subsection)
    {
        var quote = header.IndexOf('"', StringComparison.Ordinal);
        if (quote < 0)
        {
            section = header.Trim();
            subsection = null;
            return;
        }

        // Section and key names are case-insensitive in Git config; a SUBSECTION name is case-SENSITIVE, so it is
        // carried through exactly as written rather than normalised.
        section = header[..quote].Trim();
        subsection = header[(quote + 1)..].TrimEnd().TrimEnd('"');
    }

    /// <summary>
    ///     The canonical section name to write this entry under, or <see langword="null" /> when it is not preserved.
    ///     Returning the constant rather than the parsed text is what keeps a file written as <c>[CORE]</c> from
    ///     rendering as a second section beside <c>[core]</c>.
    /// </summary>
    private static string? ResolvePreservedSection(string section, string key)
    {
        if (string.Equals(section, CoreSection, StringComparison.OrdinalIgnoreCase))
        {
            return PreservedCoreKeys.Contains(key, StringComparer.OrdinalIgnoreCase) ? CoreSection : null;
        }

        // Every extension EXCEPT worktreeConfig — see the class remarks: that one names a second config file this
        // rewrite does not sanitise, and the file is removed instead.
        return string.Equals(section, ExtensionsSection, StringComparison.OrdinalIgnoreCase)
               && !string.Equals(key, "worktreeConfig", StringComparison.OrdinalIgnoreCase)
            ? ExtensionsSection
            : null;
    }

    private static string Render(List<PreservedEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var group in entries.GroupBy(static entry => (entry.Section, entry.Subsection)))
        {
            builder.Append(CultureInfo.InvariantCulture, $"[{group.Key.Section}");
            if (group.Key.Subsection is { } subsection)
            {
                builder.Append(CultureInfo.InvariantCulture, $" \"{subsection}\"");
            }

            builder.Append("]\n");
            foreach (var entry in group)
            {
                builder.Append(CultureInfo.InvariantCulture, $"\t{entry.Key} = {entry.Value}\n");
            }
        }

        return builder.ToString();
    }

    private sealed record PreservedEntry(string Section, string? Subsection, string Key, string Value);
}
