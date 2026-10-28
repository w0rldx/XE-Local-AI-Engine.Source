namespace XE_Local_AI_Engine.Tests.AgentHome;

using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

/// <summary>
///     Builds the exact command-line keys the fake sandbox provider uses to look up scripted output
///     (<c>executable + " " + string.Join(" ", arguments)</c>) for the commands the patch export issues.
/// </summary>
/// <remarks>
///     Keys include the aliases, because the export scopes its staging and both diffs to the copied folders' pathspecs.
///     They derive from <see cref="AgentHomeGit.WorkspaceArguments" />, the SAME helper the export uses (byte-stability
///     pins on top of the shared hardened set), so a flag change moves both; any other helper makes the fake stop
///     matching and the scripted output silently falls through to the default.
/// </remarks>
internal static class GitDiffCommandKeys
{
    public static string StageAll(params string[] aliases)
    {
        return CommandKey(AgentHomeGit.WorkspaceArguments(["add", "-A", "--", .. Pathspecs(aliases)]));
    }

    public static string PatchDiff(params string[] aliases)
    {
        return CommandKey(AgentHomeGit.WorkspaceArguments([
            "diff", "--cached", "--no-textconv", "--no-ext-diff", "--binary", "--find-renames=50%", "--find-copies=50%", "--src-prefix=a/", "--dst-prefix=b/", "HEAD", "--",
            .. Pathspecs(aliases)
        ]));
    }

    public static string NameStatus(params string[] aliases)
    {
        return CommandKey(AgentHomeGit.WorkspaceArguments([
            "diff", "--cached", "--no-textconv", "--no-ext-diff", "--name-status", "-z", "--find-renames=50%", "--find-copies=50%", "HEAD", "--",
            .. Pathspecs(aliases)
        ]));
    }

    public static string CheckIgnore => CommandKey(AgentHomeGit.WorkspaceArguments("check-ignore", "-z", "--stdin"));

    public static string LsFiles(params string[] workspaceRelativePaths)
    {
        return CommandKey(AgentHomeGit.WorkspaceArguments([
            "ls-files", "-z", "-t", "-v", "--cached", "--others", "--deleted", "--exclude-standard", "--",
            .. workspaceRelativePaths.Select(static path => ":(literal)" + path)
        ]));
    }

    /// <summary>The alias pathspecs the export scopes every command to, in the production form.</summary>
    private static IEnumerable<string> Pathspecs(string[] aliases)
    {
        return aliases.Select(static alias => ":(literal)" + alias);
    }

    private static string CommandKey(IReadOnlyList<string> arguments)
    {
        return AgentHomeGit.Executable + " " + string.Join(" ", arguments);
    }
}
