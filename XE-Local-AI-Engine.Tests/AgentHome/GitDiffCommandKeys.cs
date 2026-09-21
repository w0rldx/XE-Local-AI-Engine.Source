namespace XE_Local_AI_Engine.Tests.AgentHome;

using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

/// <summary>
///     Builds the exact command-line keys the fake sandbox provider uses to look up scripted output
///     (<c>executable + " " + string.Join(" ", arguments)</c>), for the two <c>git diff</c> commands the
///     patch export issues. Deriving the keys from <see cref="AgentHomeGit" /> keeps the test in lockstep with the
///     production command shape — if the hardened git flags change, both move together. It must use the SAME
///     helper the export uses (<see cref="AgentHomeGit.WorkspaceArguments" />, which adds the workspace's
///     byte-stability pins on top of the shared hardened set), or the fake stops matching and the scripted
///     output silently falls through to the default.
/// </summary>
internal static class GitDiffCommandKeys
{
    public static string StageAll => CommandKey(AgentHomeGit.WorkspaceArguments("add", "-A", "--", "."));

    public static string PatchDiff => CommandKey(AgentHomeGit.WorkspaceArguments("diff", "--cached", "--no-textconv", "--no-ext-diff", "--binary", "--find-renames=50%", "--find-copies=50%", "--src-prefix=a/", "--dst-prefix=b/", "HEAD", "--", "."));

    public static string NameStatus => CommandKey(AgentHomeGit.WorkspaceArguments("diff", "--cached", "--no-textconv", "--no-ext-diff", "--name-status", "-z", "--find-renames=50%", "--find-copies=50%", "HEAD", "--", "."));

    public static string CheckIgnore => CommandKey(AgentHomeGit.WorkspaceArguments("check-ignore", "-z", "--stdin"));

    public static string LsFiles(params string[] workspaceRelativePaths)
    {
        return CommandKey(AgentHomeGit.WorkspaceArguments([
            "ls-files", "-z", "-t", "-v", "--cached", "--others", "--deleted", "--exclude-standard", "--",
            .. workspaceRelativePaths.Select(static path => ":(literal)" + path)
        ]));
    }

    private static string CommandKey(IReadOnlyList<string> arguments)
    {
        return AgentHomeGit.Executable + " " + string.Join(" ", arguments);
    }
}
