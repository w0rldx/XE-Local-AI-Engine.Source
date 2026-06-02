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
        "-c", "core.quotePath=false"
    ];

    /// <summary>
    ///     Builds a git argument list prefixed with the byte-stabilizing <c>-c</c> flags: hooks
    ///     and the global attributes file are disabled so neither host hooks nor a copied <c>.gitattributes</c> can
    ///     alter the baseline or diff bytes, and <c>core.quotePath=false</c> emits non-ASCII path bytes literally so the
    ///     <c>--name-status</c> parser maps a copied folder's <c>&lt;alias&gt;/…</c> path correctly instead of seeing a
    ///     C-style quoted, escaped path.
    /// </summary>
    public static IReadOnlyList<string> Arguments(params string[] tail) => [.. HardenedConfig, .. tail];
}
