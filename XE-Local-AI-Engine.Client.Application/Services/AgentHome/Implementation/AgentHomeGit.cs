namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

/// <summary>
///     Shared constants and argument helpers for the in-sandbox git commands used by the workspace baseline and the
///     patch export.
/// </summary>
/// <remarks>
///     Centralizing the executable, the workspace root and the byte-stabilizing <c>-c</c> flags keeps the baseline
///     and the diff consistent: the baseline must be created with the same hardened configuration the diff is later
///     taken under, or the diff bytes drift under a copied <c>.gitattributes</c>.
/// </remarks>
internal static class AgentHomeGit
{
    /// <summary>The git executable run inside the sandbox.</summary>
    public const string Executable = "git";

    /// <summary>
    ///     The in-sandbox repository root for the copied selected folders. The baseline <c>git init</c> runs here and
    ///     each selected folder is an <c>&lt;alias&gt;</c> subdirectory, so diff paths are <c>&lt;alias&gt;/&lt;rel&gt;</c>.
    /// </summary>
    public const string WorkspaceSelectedRoot = "/agent-home/workspace/selected";

    private static readonly string[] HardenedConfig =
    [
        "-c", "core.hooksPath=/dev/null",
        "-c", "core.attributesfile=/dev/null",
        "-c", "core.quotePath=false",

        // Everything below neutralizes repository-local configuration git would otherwise execute as a host-side command:
        // a command-line -c outranks every config file, include.path chains included. core.fsmonitor is the live one.
        "-c", "core.fsmonitor=",

        // Not reachable from the current command set (no network operation, stdout always redirected so no pager spawns),
        // pinned anyway so a later fetch or pager cannot re-open the hole. Empty means unset, /bin/false fails closed.
        "-c", "core.sshCommand=",
        "-c", "core.pager=cat",
        "-c", "core.editor=false"
    ];

    /// <summary>
    ///     Builds a git argument list prefixed with the byte-stabilizing and exec-suppressing <c>-c</c> flags.
    /// </summary>
    /// <remarks>
    ///     Hooks and the global attributes file are disabled so neither host hooks nor a copied <c>.gitattributes</c> can alter
    ///     baseline or diff bytes, <c>core.quotePath=false</c> emits non-ASCII path bytes literally so the <c>--name-status</c> parser
    ///     maps a copied folder's <c>&lt;alias&gt;/…</c> path instead of a C-quoted one, and the exec-bearing keys with fixed names
    ///     are pinned. They do NOT close an attribute-selected <c>filter.*.clean</c> or <c>diff.*.textconv</c> driver: that class is
    ///     closed by <see cref="AgentHomeGitHardening" />. See <c>docs/wiki/04-agent-mode.md</c> §2.2.
    /// </remarks>
    public static IReadOnlyList<string> Arguments(params string[] tail)
    {
        return [.. HardenedConfig, .. tail];
    }

    /// <summary>
    ///     The byte-stability settings the AgentHome WORKSPACE repository needs on top of
    ///     <see cref="Arguments(string[])" />: the baseline and the later diff must agree about line endings and the
    ///     executable bit, or the diff reports changes nobody made.
    /// </summary>
    /// <remarks>
    ///     Use it for the two sites that own the AgentHome workspace copy — the baseline and the patch export — and nothing else,
    ///     because the pair must match on both. They ride the COMMAND LINE, not the repository config, because
    ///     <see cref="AgentHomeGitHardening.TryHardenWorkspaceRepositoryAsync" /> rewrites that config to a node-owned allow-list
    ///     before every invocation, so a stored value would be gone by the diff. They stay out of <see cref="HardenedConfig" />,
    ///     which operator-checkout callers share: see <c>docs/wiki/04-agent-mode.md</c> §2.2.
    /// </remarks>
    public static IReadOnlyList<string> WorkspaceArguments(params string[] tail)
    {
        return [.. HardenedConfig, "-c", "core.autocrlf=false", "-c", "core.filemode=false", .. tail];
    }
}
