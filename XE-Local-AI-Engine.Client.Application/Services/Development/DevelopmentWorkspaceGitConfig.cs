namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Globalization;
using System.Text;

/// <summary>
///     Rewrites the managed workspace's <c>.git/config</c> to a known-good minimal file immediately before the engine
///     runs host-side Git against that workspace.
/// </summary>
/// <remarks>
///     A repository-local config can make Git execute arbitrary commands on the machine running it, and a <c>-c</c>
///     pin cannot close <c>filter.*.clean</c> because driver names are arbitrary; removing every definition closes
///     that, <c>core.fsmonitor</c> and any future exec-bearing key at once. Minimal is not empty, because Git refuses
///     to operate on a repository whose <c>extensions.*</c> keys are missing. What is kept and why is in
///     <c>docs/wiki/12-security-and-privacy.md</c> ("The managed workspace's Git configuration is engine-owned").
/// </remarks>
internal static class DevelopmentWorkspaceGitConfig
{
    private const string CoreSection = "core";
    private const string ExtensionsSection = "extensions";

    /// <summary>
    ///     The <c>core</c> keys that describe the repository rather than instructing Git to run something.
    /// </summary>
    /// <remarks>
    ///     Everything else in <c>core</c> is dropped, which makes this an allow-list rather than a block-list: a key
    ///     nobody has thought of yet is dropped by default instead of surviving until someone remembers to name it.
    ///     Never add <c>whitespace</c> or <c>autocrlf</c> — a clone carries neither, and a key an agent-writable file
    ///     supplies is what this list exists to refuse.
    /// </remarks>
    private static readonly string[] PreservedCoreKeys = ["repositoryformatversion", "filemode", "bare", "symlinks", "ignorecase"];

    /// <summary>
    ///     Removes every configured value from the workspace's <c>.git/config</c> except the repository-describing keys
    ///     Git needs to open the repository at all. A no-op when the workspace has no Git directory.
    /// </summary>
    /// <exception cref="DevelopmentWorkspaceSecurityException">
    ///     The workspace's <c>.git</c> is a symbolic link, which means the path the engine is about to rewrite is not
    ///     the one it created.
    /// </exception>
    public static async Task RestoreMinimalAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var gitDirectory = Path.Combine(workspacePath, ".git");
        if (!Directory.Exists(gitDirectory))
        {
            // Either the workspace is not materialised yet, or its .git is a linked worktree's pointer FILE, whose
            // repository-local config lives elsewhere. The standalone-clone assertion rejects that second case.
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

        // Deleted rather than overwritten in place: a command inside the workspace can replace the file with a symlink
        // that an ordinary write would follow out. Deleting removes the redirection before anything is written.
        DeleteIfPresent(configPath);
        DeleteIfPresent(Path.Combine(gitDirectory, "config.worktree"));

        await File.WriteAllTextAsync(configPath, Render(preserved), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
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

    /// <summary>Parses the existing config far enough to keep the preserved keys, and no further.</summary>
    /// <remarks>
    ///     Deliberately naive about Git's odder syntax — backslash line continuation, a quoted value spanning a
    ///     newline. It can afford to be, because the allow-list makes every parse error fail safe: a continuation
    ///     line misread as a section header can only drop a key, never admit an exec-bearing one.
    /// </remarks>
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
                entries.Add(new PreservedEntry { Section = canonicalSection, Subsection = subsection, Key = key, Value = value });
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

    private sealed record PreservedEntry
    {
        public required string Section { get; init; }

        public required string? Subsection { get; init; }

        public required string Key { get; init; }

        public required string Value { get; init; }
    }
}
