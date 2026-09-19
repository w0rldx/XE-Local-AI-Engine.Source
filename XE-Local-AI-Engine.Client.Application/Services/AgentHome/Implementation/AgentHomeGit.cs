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
        // core.fsmonitor is the one that was live. Measured on a current Git release: a value planted in a repository-local
        // .git/config runs as a shell command on the first index refresh — status, reset, add and diff all trigger it —
        // under this exact hardened argument vector. That is reachable from the host, not just the sandbox, because
        // DevelopmentPatchEvidenceService runs `reset` and `add -A` with WorkingDirectory set to the workspace, and
        // the workspace .git/config is writable from inside the container.
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
    ///         <strong>What these flags do NOT close, and what does.</strong> A <c>filter.&lt;driver&gt;.clean</c> or
    ///         <c>diff.&lt;name&gt;.textconv</c> defined in configuration and selected by an <em>in-tree</em>
    ///         <c>.gitattributes</c> still executes on <c>add</c> and on <c>diff</c>. Driver names are arbitrary, so
    ///         there is no finite key set to pin here, and git has no flag that disables attribute processing —
    ///         <c>core.attributesfile=/dev/null</c> only disables the <em>global</em> attributes file, which an in-tree
    ///         <c>.gitattributes</c> outranks. It is closed OUTSIDE this class, by
    ///         <see cref="AgentHomeGitHardening" />: a driver has to be DEFINED in configuration to run, so making
    ///         every configuration git can reach node-owned closes the whole class at once without enumerating a key.
    ///         Every AgentHome git invocation goes through that guard and carries
    ///         <see cref="AgentHomeGitHardening.Environment" />; these <c>-c</c> pins remain the byte-stabilizing half
    ///         and a second, independent cut at the exec-bearing keys that do have fixed names.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string> Arguments(params string[] tail)
    {
        return [.. HardenedConfig, .. tail];
    }

    /// <summary>
    ///     The byte-stability settings the AgentHome WORKSPACE repository needs on top of
    ///     <see cref="Arguments(string[])" />: the baseline and the later diff must agree about line endings and the
    ///     executable bit, or the diff reports changes nobody made.
    ///     <para>
    ///         They are carried on the COMMAND LINE, and they are NOT in <see cref="HardenedConfig" />. Two separate
    ///         reasons, both load-bearing. The command line, because
    ///         <see cref="AgentHomeGitHardening.TryHardenWorkspaceRepositoryAsync" /> rewrites the repository's own
    ///         config to a node-owned allow-list before every invocation, so a value the baseline had STORED there
    ///         would be dropped before the diff and the two sides would stop agreeing. And not in the shared set,
    ///         because <see cref="Arguments(string[])" /> is also what Development Mode, Dev Workflows, knowledge
    ///         repository import and host patch APPLY run git with — those operate on the operator's own checkouts,
    ///         where forcing <c>core.autocrlf</c> would change how a patch renders or applies on a repository that
    ///         legitimately stores CRLF. Development Mode derives that policy from the repository's own index
    ///         (<c>DevelopmentWorkspaceWhitespacePolicy</c>) precisely so that it is not forced.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     Use this for the two sites that own the AgentHome workspace copy — the baseline and the patch export — and
    ///     nothing else. The pair must match on both, or the comparison is between two different normalizations.
    /// </remarks>
    public static IReadOnlyList<string> WorkspaceArguments(params string[] tail)
    {
        return [.. HardenedConfig, "-c", "core.autocrlf=false", "-c", "core.filemode=false", .. tail];
    }
}
