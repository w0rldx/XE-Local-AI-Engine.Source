namespace XE_Local_AI_Engine.Client.Services.Agents;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Imports third-party Agent Skills into the node-local library. This is the feature's security boundary: every
///     input here is attacker-authored content (an archive an operator was handed, a repository anyone can publish to),
///     so the pipeline is <em>two-phase and dry-run first</em> — a preview call parses, guards and reports without
///     writing a single row, and a second call persists the payload the operator actually saw.
///     <para>
///         The second call replays the <em>materialised preview payload</em>; it never re-parses the upload or re-fetches
///         the repository. Re-deriving the content would reopen the exact divergence the two phases exist to prevent —
///         a repository can change between the two calls, so the operator would be approving one payload and persisting
///         another.
///     </para>
///     <para>
///         Imported skills always land <see cref="AgentSkillRecord.Enabled" /> = <c>false</c> with
///         <see cref="AgentSkillOrigin.Imported" /> provenance. That is the strongest control in the design: the
///         definition resolver only resolves <em>enabled</em> skills, so third-party instructions cannot reach a model
///         until an operator deliberately turns them on.
///     </para>
///     <para>
///         Scripts are never imported (locked decision 1). They are detected, listed in the report as refused, and
///         dropped — the feature adds no execution surface.
///     </para>
/// </summary>
public interface ISkillImportService
{
    /// <summary>
    ///     Phase 1 for an uploaded <c>.zip</c>. Extracts in memory under the archive guards, discovers every
    ///     <c>SKILL.md</c>, and returns the report. Writes nothing.
    /// </summary>
    /// <exception cref="SkillImportException">An archive guard tripped, or the archive holds no skill.</exception>
    Task<SkillImportPreview> PreviewArchiveAsync(ReadOnlyMemory<byte> archive, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Phase 1 for a pasted raw <c>SKILL.md</c>. A pasted document has no containing directory and no bundled
    ///     files, so the frontmatter <c>name</c> is authoritative and the skill imports instructions-only. Writes nothing.
    /// </summary>
    /// <exception cref="SkillImportException">The document carries no parsable frontmatter block.</exception>
    Task<SkillImportPreview> PreviewMarkdownAsync(string skillMarkdown, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Phase 1 for a GitHub repository. <paramref name="owner" /> and <paramref name="repository" /> are the only
    ///     caller-supplied parts of the URL — a pasted URL is never accepted, which is what keeps the host allowlist
    ///     meaningful. A large collection repository yields many candidates; the operator selects, we never bulk-import.
    ///     Writes nothing.
    /// </summary>
    /// <exception cref="SkillImportException">The owner/repo is malformed, the download failed, or a guard tripped.</exception>
    Task<SkillImportPreview> PreviewGitHubRepositoryAsync(string owner, string repository, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Phase 2. Persists the selected skills from a previously issued preview. Fails — writing nothing — unless
    ///     <see cref="SkillImportCommitRequest.Acknowledged" /> is <c>true</c> and the token is still live and unused.
    /// </summary>
    /// <exception cref="SkillImportException">Unacknowledged, unknown/expired token, or an unselectable skill name.</exception>
    Task<SkillImportResult> CommitAsync(SkillImportCommitRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
///     The import guard limits, bindable so an operator can tighten them without a rebuild. The defaults are sized to
///     admit a real collection repository — an archive of ~175 skills is well past a thousand entries and tens of
///     megabytes — while keeping the guards that actually bound memory tight.
/// </summary>
/// <remarks>
///     The ranking matters if you change these. <see cref="MaxEntryBytes" /> and <see cref="MaxTotalInflatedBytes" />
///     are the real guards: they bound what is inflated, and only entries the import intends to keep are inflated at
///     all. <see cref="MaxEntries" /> bounds a central-directory walk, which is cheap — set too low it blocks ordinary
///     repositories while buying almost nothing, which is what the original 512 did.
/// </remarks>
public sealed class SkillImportOptions
{
    public const string SectionName = "SkillImport";

    /// <summary>Entry-count cap. Bounds enumeration cost before a single byte is inflated.</summary>
    public int MaxEntries { get; set; } = 8192;

    /// <summary>Hard cap on the archive as received.</summary>
    public int MaxArchiveBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>Per-entry inflated cap. Skill content is text; a megabyte of markdown is already implausible.</summary>
    public int MaxEntryBytes { get; set; } = 1024 * 1024;

    /// <summary>Total inflated cap across every entry kept.</summary>
    public int MaxTotalInflatedBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>Per-entry inflation ratio cap. A legitimate text file does not compress a hundredfold.</summary>
    public int MaxCompressionRatio { get; set; } = 100;

    /// <summary>
    ///     Bundled files one skill may carry. The whole-archive caps alone would let a single skill hold hundreds of
    ///     resources — every one of them a name and a description the model is shown when the skill loads.
    /// </summary>
    public int MaxResourcesPerSkill { get; set; } = 64;
}

/// <summary>
///     The dry-run report the operator approves. <see cref="Token" /> is a short-lived, single-use handle to the
///     materialised payload behind this report — the payload phase 2 persists verbatim.
/// </summary>
/// <param name="Token">Handle for <see cref="ISkillImportService.CommitAsync" />. Expires; consumed on a successful commit.</param>
/// <param name="SourceUri">Provenance as it will be persisted: the literal <c>upload</c>, or <c>github:owner/repo</c>.</param>
/// <param name="Skills">Every skill discovered in the source, ordered by name.</param>
/// <param name="Warnings">Source-level notes that block nothing (e.g. a frontmatter name that disagreed with its directory).</param>
public sealed record SkillImportPreview(Guid Token, string SourceUri, IReadOnlyList<SkillImportCandidate> Skills, IReadOnlyList<string> Warnings);

/// <summary>
///     One discovered skill, exactly as it would be written. <see cref="Body" /> and <see cref="Resources" /> are
///     carried so the operator reviews the real content and so phase 2 has nothing left to re-derive.
/// </summary>
/// <param name="Name">The skill name that will be persisted — the containing directory name when the source had one.</param>
/// <param name="RefusedScripts">Script files found and dropped. Listed because an operator should see what a skill expected to run.</param>
/// <param name="ConflictsWithExistingSkill">A skill with this name (NOCASE) is already in the library; the commit's conflict resolution decides.</param>
/// <param name="Problems">Non-empty means this skill cannot be imported at all. Messages never echo untrusted content.</param>
public sealed record SkillImportCandidate(
    string Name,
    string Description,
    string Body,
    string? License,
    string? Compatibility,
    string? AllowedTools,
    IReadOnlyDictionary<string, string>? Metadata,
    int BodySizeBytes,
    int BodyLineCount,
    IReadOnlyList<SkillImportResource> Resources,
    IReadOnlyList<string> RefusedScripts,
    bool ConflictsWithExistingSkill,
    IReadOnlyList<string> Problems)
{
    /// <summary>True when nothing blocks this skill from being persisted.</summary>
    public bool CanImport => Problems.Count == 0;
}

/// <summary>One bundled file that passed the extension allowlist, the name charset guard and UTF-8 validation.</summary>
public sealed record SkillImportResource(string Name, string Description, string MediaType, string Content, int SizeBytes);

/// <summary>Phase 2 input: which skills from <paramref name="Token" />'s report to write, and the operator's explicit consent.</summary>
/// <param name="Acknowledged">
///     Must be <c>true</c>. An import that is not explicitly acknowledged fails and writes nothing — the operator is
///     confirming they read a preview of third-party instructions that will run with their agent's tool access.
/// </param>
public sealed record SkillImportCommitRequest(
    Guid Token,
    IReadOnlyList<string> SkillNames,
    SkillImportConflictResolution ConflictResolution = SkillImportConflictResolution.Skip,
    bool Acknowledged = false);

/// <summary>What to do when the library already holds a skill with the imported name.</summary>
public enum SkillImportConflictResolution
{
    /// <summary>Leave the existing skill untouched. The default: silently destroying operator content is the worst available outcome.</summary>
    Skip = 0,

    /// <summary>Overwrite the existing skill's content and resources. Loses local edits.</summary>
    Replace = 1
}

/// <summary>Per-skill outcome of a commit.</summary>
public sealed record SkillImportResult(IReadOnlyList<SkillImportOutcome> Outcomes);

/// <summary>What happened to one selected skill. <paramref name="Reason" /> is operator-safe and echoes no imported content.</summary>
public sealed record SkillImportOutcome(string Name, SkillImportStatus Status, string? Reason = null);

/// <summary>Terminal state of one skill in a commit.</summary>
public enum SkillImportStatus
{
    /// <summary>Written as a new, disabled, <see cref="AgentSkillOrigin.Imported" /> skill.</summary>
    Imported = 0,

    /// <summary>An existing skill's content and resources were overwritten.</summary>
    Replaced = 1,

    /// <summary>Not written — a name conflict was resolved as <see cref="SkillImportConflictResolution.Skip" />.</summary>
    Skipped = 2
}

/// <summary>
///     Thrown when an import is refused. Every guard in the pipeline fails closed through this exception, and its
///     message is written to be shown to the operator: it names the rule that was broken and deliberately never echoes
///     an entry path, a resource name or any imported text, because those are the injection sinks the guards exist to
///     close.
/// </summary>
public sealed class SkillImportException : Exception
{
    public SkillImportException(string message) : base(message)
    {
    }

    public SkillImportException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
