namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Makes every git configuration the NODE's own git invocations read node-owned, immediately before each one.
/// </summary>
/// <remarks>
///     Git executes programs named by configuration (<c>diff.*.textconv</c>, <c>diff.external</c>, <c>filter.*.clean</c>, <c>core.fsmonitor</c>),
///     and an in-tree <c>.gitattributes</c> only NAMES a driver, so the class closes if and only if no configuration git can reach was
///     written by anyone but the node: the repository-local file is rewritten to the node's allow-list
///     (<see cref="DevelopmentWorkspaceGitConfig" />, reused not re-implemented), <c>GIT_CONFIG_GLOBAL</c> points at the null device and
///     <c>GIT_CONFIG_NOSYSTEM</c> drops the system file. <c>--no-textconv</c>/<c>--no-ext-diff</c> are belt and braces, not the control.
/// </remarks>
internal static class AgentHomeGitHardening
{
    /// <summary>
    ///     The null device, as the value for <c>GIT_CONFIG_GLOBAL</c>.
    /// </summary>
    /// <remarks>
    ///     A device rather than a file inside some node-owned directory ON PURPOSE: <c>run_command</c> can write
    ///     anywhere the host user can, so any regular path the node nominated could be turned into a config file,
    ///     while nothing can make the null device one.
    /// </remarks>
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
    /// </summary>
    /// <returns>
    ///     <see langword="true" /> when git may now be run against the workspace; <see langword="false" /> when the
    ///     caller must refuse, because the workspace's repository is not the node's any more.
    /// </returns>
    /// <remarks>
    ///     The <c>.git</c> check is not paranoia: a <c>.git</c> replaced by a GITFILE pointing at a model-owned directory defeats
    ///     the rewrite — it finds no directory, returns silently, and git reads the model's configuration from wherever the pointer
    ///     leads. The workspace copy never holds a source repository's own <c>.git</c> (copy exclusion set), so the only one here is
    ///     the baseline's and anything else means it was replaced. A provider naming no host directory — the deterministic fake
    ///     backs its workspace with a virtual filesystem — has no file to rewrite and no host git to protect, so this reports success.
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
    ///     directory.
    /// </summary>
    /// <remarks>
    ///     The process jail identity-maps its root, which is what makes the sandbox-absolute workspace path resolvable
    ///     to a host path the engine can rewrite a file at.
    /// </remarks>
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
