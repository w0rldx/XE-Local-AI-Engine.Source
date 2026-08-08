namespace XE_Local_AI_Engine.Tests.AgentHome;

using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

/// <summary>
///     Builds the exact command-line keys the fake sandbox provider uses to look up scripted output
///     (<c>executable + " " + string.Join(" ", arguments)</c>), for the two <c>git diff</c> commands the
///     patch export issues. Deriving the keys from <see cref="AgentHomeGit" /> keeps the test in lockstep with the
///     production command shape — if the hardened git flags change, both move together.
/// </summary>
internal static class GitDiffCommandKeys
{
    public static string PatchDiff => CommandKey(AgentHomeGit.Arguments("diff", "--binary", "--find-renames=50%", "--find-copies=50%", "--src-prefix=a/", "--dst-prefix=b/", "HEAD", "--", "."));

    public static string NameStatus => CommandKey(AgentHomeGit.Arguments("diff", "--name-status", "--find-renames=50%", "--find-copies=50%", "HEAD", "--", "."));

    private static string CommandKey(IReadOnlyList<string> arguments)
    {
        return AgentHomeGit.Executable + " " + string.Join(" ", arguments);
    }
}
