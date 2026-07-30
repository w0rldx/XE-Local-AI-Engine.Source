namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

/// <summary>
///     Shared constants and argument helpers for the in-sandbox git commands used by the workspace baseline (workspace copy)
///     and patch export. Centralizing the executable, the workspace root, and the byte-stabilizing
///     <c>-c</c> flags keeps the baseline and the diff consistent — the baseline must be created
///     with the same hardened configuration the diff is later taken under, or the diff bytes drift under copied
///     <c>.gitattributes</c>.
/// </summary>
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

        // Everything below neutralizes repository-local configuration that git would otherwise execute as a host-side
        // command. A command-line -c outranks every config file, including anything reached through an include.path /
        // includeIf chain, so pinning the key here is what makes the repository's own .git/config unable to supply it.
        //
        // core.fsmonitor is the one that was live. Measured on git 2.53.0: a value planted in a repository-local
        // .git/config runs as a shell command on the first index refresh — status, reset, add and diff all trigger it —
        // under this exact hardened argument vector. That is reachable from the host, not just the sandbox, because
        // DevelopmentPatchEvidenceService runs `reset` and `add -A` with WorkingDirectory set to the workspace, and
        // under D8 the workspace .git/config is writable from inside the container.
        "-c", "core.fsmonitor=",

        // These three were measured as NOT reachable from the current command set — no network operation, and stdout is
        // always redirected so no pager is spawned. They are pinned anyway so that adding a command later (a fetch, or
        // anything that pages) cannot quietly re-open the hole. Empty means "unset", and /bin/false as an editor fails
        // closed rather than opening one.
        "-c", "core.sshCommand=",
        "-c", "core.pager=cat",
        "-c", "core.editor=false"
    ];

    /// <summary>
    ///     Builds a git argument list prefixed with the byte-stabilizing and exec-suppressing <c>-c</c> flags: hooks
    ///     and the global attributes file are disabled so neither host hooks nor a copied <c>.gitattributes</c> can
    ///     alter the baseline or diff bytes, <c>core.quotePath=false</c> emits non-ASCII path bytes literally so the
    ///     <c>--name-status</c> parser maps a copied folder's <c>&lt;alias&gt;/…</c> path correctly instead of seeing a
    ///     C-style quoted, escaped path, and the exec-bearing keys are pinned so repository-local configuration cannot
    ///     turn a git invocation into host-side command execution.
    ///     <para>
    ///         <strong>Not covered here, by necessity:</strong> a <c>filter.&lt;driver&gt;.clean</c> defined in
    ///         repository-local config and selected by an <em>in-tree</em> <c>.gitattributes</c> still executes on
    ///         <c>git add</c>. <c>core.attributesfile=/dev/null</c> only disables the <em>global</em> attributes file —
    ///         in-tree <c>.gitattributes</c> outranks it — and git has no command-line flag that disables attribute
    ///         processing, so it is not closable from here. It is closed instead by the mount boundary: S3.5 binds
    ///         <c>&lt;workspace&gt;/.git/config</c> read-only into the container, and a filter driver that cannot be
    ///         <em>defined</em> cannot run, whatever the in-tree <c>.gitattributes</c> selects. That mount exists only
    ///         under the container provider, so until S3.12 switches Development off the process provider these pins are
    ///         the only mitigation in force and they do not cover that vector.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string> Arguments(params string[] tail)
    {
        return [.. HardenedConfig, .. tail];
    }
}
