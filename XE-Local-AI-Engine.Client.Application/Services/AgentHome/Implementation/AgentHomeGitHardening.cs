namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Makes every git configuration the NODE's own git invocations read node-owned, immediately before each one.
///     <para>
///         <b>Why it exists.</b> Git executes programs named by configuration: <c>diff.&lt;name&gt;.textconv</c> and
///         <c>diff.external</c> on a diff, <c>filter.&lt;driver&gt;.clean</c> on anything that converts worktree
///         content (including a plain <c>git diff</c> against HEAD), <c>core.fsmonitor</c> on an index refresh. An
///         in-tree <c>.gitattributes</c> only NAMES a driver — the program comes from config — so the whole class
///         closes if, and only if, no configuration git can reach was written by anyone but the node.
///         <c>AgentHomeGit</c>'s <c>-c</c> pins cannot do that: driver names are arbitrary, so there is no finite key
///         set to pin, and git has no flag that disables attribute processing.
///     </para>
///     <para>
///         <b>Why this became load-bearing.</b> Before the goal executor the model could not write, so the gap was
///         inert. Now <c>run_command</c> can set repository-local config and <c>write_file</c> can create a
///         <c>.gitattributes</c> (only <c>.git</c> paths are refused) — and the node's patch export runs git over that
///         same tree AFTER the model's turn has ended, outside the run's budgets. Measured on git 2.53.0 against the
///         exact export argument vector: both a repository-local <c>textconv</c> and a repository-local
///         <c>filter.clean</c> executed. <c>--no-textconv</c>/<c>--no-ext-diff</c> do NOT close it — the clean filter
///         still ran — so they are belt and braces here, not the control.
///     </para>
///     <para>
///         <b>The three reachable configuration sources, and what closes each.</b> Repository-local
///         <c>.git/config</c>: rewritten to the node's allow-list minimal file (see
///         <see cref="DevelopmentWorkspaceGitConfig" /> — reused rather than re-implemented, so there is one such
///         rewrite in the codebase). The global file (<c>$HOME/.gitconfig</c> or
///         <c>$XDG_CONFIG_HOME/git/config</c>, both reachable because the sandbox forwards <c>HOME</c>): closed by
///         <c>GIT_CONFIG_GLOBAL</c> pointed at the null DEVICE, which is not a path anything can turn into a config
///         file. The system file: closed by <c>GIT_CONFIG_NOSYSTEM</c>. All three were measured executing before the
///         guard and not executing after it.
///     </para>
///     <para>
///         <b>Residual, stated plainly.</b> (1) The rewrite happens immediately before the invocation, so a process a
///         <c>run_command</c> deliberately double-forked and left behind could in principle rewrite the file again in
///         the window between. This is the same window Development Mode accepts for the same mechanism; closing it
///         needs a git directory the sandboxed command cannot reach at all, which the process jail — a soft jail with
///         no filesystem boundary, by declaration — cannot provide anywhere on this host. (2) A model that rewrote
///         the git OBJECTS or HEAD could make the exported patch misrepresent the tree; that is an integrity limit on
///         what a patch proves, not host execution, and it is bounded by the operator reviewing the patch.
///     </para>
/// </summary>
internal static class AgentHomeGitHardening
{
    /// <summary>
    ///     The null device, as the value for <c>GIT_CONFIG_GLOBAL</c>. A device rather than a file inside some
    ///     node-owned directory ON PURPOSE: <c>run_command</c> can write anywhere the host user can, so any regular
    ///     path the node nominated could be turned into a config file, while nothing can make the null device one.
    /// </summary>
    private static string NullDevicePath => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    /// <summary>
    ///     The environment every node git invocation carries, layered over the sandbox's own allow-list. It removes the
    ///     global and system configuration files from git's search entirely, so the repository-local rewrite is the
    ///     whole of what git reads.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Environment { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["GIT_CONFIG_GLOBAL"] = NullDevicePath,
        // The system attributes file, for the same reason core.attributesfile pins the global one.
        ["GIT_ATTR_NOSYSTEM"] = "1",
        // No invocation here is a network operation, but a credential helper is a configured PROGRAM like the rest;
        // refusing a prompt outright keeps a future command from blocking on one.
        ["GIT_TERMINAL_PROMPT"] = "0"
    };

    /// <summary>
    ///     Rewrites the workspace repository's configuration to the node's minimal allow-list, and fails closed when
    ///     the repository is not the one the node created.
    ///     <para>
    ///         The <c>.git</c> check is not paranoia about an unlikely shape: a <c>.git</c> replaced by a GITFILE
    ///         pointing at a model-owned directory was measured defeating the rewrite completely — the rewrite finds no
    ///         directory to rewrite, returns silently, and git then reads the model's configuration from wherever the
    ///         pointer leads. The workspace copy never contains a source repository's own <c>.git</c> (it is in the
    ///         copy exclusion set), so the only <c>.git</c> here is the one the baseline's <c>git init</c> created, and
    ///         anything else means it was replaced.
    ///     </para>
    /// </summary>
    /// <returns>
    ///     <see langword="true" /> when git may now be run against the workspace; <see langword="false" /> when the
    ///     caller must refuse, because the workspace's repository is not the node's any more.
    /// </returns>
    /// <remarks>
    ///     A provider that names no host directory for its sandbox (the deterministic fake backs its workspace with a
    ///     virtual filesystem) has no file to rewrite and no host git to protect, so the guard reports success without
    ///     touching anything.
    /// </remarks>
    public static async Task<bool> TryHardenWorkspaceRepositoryAsync(SandboxHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (ResolveHostWorkspacePath(handle) is not { } workspacePath || !Directory.Exists(workspacePath))
        {
            return true;
        }

        var gitDirectory = new DirectoryInfo(Path.Combine(workspacePath, ".git"));
        if (!gitDirectory.Exists || gitDirectory.LinkTarget is not null)
        {
            // A missing directory here is either a gitfile, a symlink, a plain file, or nothing at all. The baseline
            // created a real directory, so every one of those means the repository was replaced.
            return false;
        }

        await DevelopmentWorkspaceGitConfig.RestoreMinimalAsync(workspacePath, cancellationToken);
        return true;
    }

    /// <summary>
    ///     The host path of the in-sandbox workspace root, or <see langword="null" /> when the provider names no host
    ///     directory. The process jail identity-maps its root, which is what makes the sandbox-absolute workspace path
    ///     resolvable to a host path the engine can rewrite a file at.
    /// </summary>
    private static string? ResolveHostWorkspacePath(SandboxHandle handle)
    {
        if (handle.WorkingRoot is not { Length: > 0 } root)
        {
            return null;
        }

        var relative = AgentHomeGit.WorkspaceSelectedRoot.TrimStart('/');
        return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
