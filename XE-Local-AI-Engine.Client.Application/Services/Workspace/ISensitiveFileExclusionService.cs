namespace XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Decides whether a selected-folder entry must be excluded from the sandbox workspace copy. Excludes secrets (<c>.env</c>, <c>.ssh</c>, credential bundles), the host <c>.git</c> baseline, and
///     generated/heavy output directories (<c>bin</c>, <c>obj</c>, <c>node_modules</c>, …). Matching is by entry name
///     so the rule applies at any depth; the caller never logs host paths.
/// </summary>
public interface ISensitiveFileExclusionService
{
    /// <summary>
    ///     Returns <see langword="true" /> when an entry with the given name (a single path segment, not a full path)
    ///     must be excluded. When the entry is a directory the caller must not descend into it.
    /// </summary>
    bool IsExcluded(string entryName, bool isDirectory);

    /// <summary>
    ///     The exact entry-name glob patterns that <see cref="IsExcluded" /> matches (e.g. <c>.git</c>, <c>.env</c>,
    ///     <c>node_modules</c>, <c>.env.*</c>, <c>*.enc</c>). Exposed so a grep-backed search can pass each as
    ///     an <c>--exclude-dir</c>/<c>--exclude</c> flag and never let an excluded file's content enter its output in
    ///     the first place — the authoritative source for that flag set lives here, not duplicated at the call site.
    /// </summary>
    IReadOnlyList<string> ExcludedEntryNames { get; }

    /// <summary>
    ///     Returns <see langword="true" /> when an entry name is CREDENTIAL-BEARING — the strict subset of
    ///     <see cref="IsExcluded" /> that answers "is this a secret?" rather than "should this have been copied?".
    ///     <para>
    ///         The two questions are genuinely different and must not be conflated. <see cref="IsExcluded" /> is a
    ///         workspace-COPY filter, so it also names generated output (<c>bin</c>, <c>obj</c>, <c>node_modules</c>,
    ///         <c>dist</c>) that is merely large and uninteresting to copy. Gating a READ on that broader set breaks
    ///         the thing an agent most needs to do after a failed build — read <c>obj/project.assets.json</c> — while
    ///         protecting nothing, because build output is not a credential. Read paths gate on this predicate; the
    ///         copy filter keeps using <see cref="IsExcluded" />.
    ///     </para>
    /// </summary>
    bool IsSecret(string entryName);

    /// <summary>
    ///     The credential-bearing subset of <see cref="ExcludedEntryNames" />, in the same glob form, for a read path
    ///     that needs the <c>--exclude</c>/<c>--exclude-dir</c> flag set without suppressing build output.
    /// </summary>
    IReadOnlyList<string> SecretEntryNames { get; }
}
