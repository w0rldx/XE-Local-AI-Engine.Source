namespace XE_Local_AI_Engine.Client.Services.Agents;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Imports third-party Agent Skills into the node-local library, and is the feature's SECURITY BOUNDARY: every
///     input here is attacker-authored content.
/// </summary>
/// <remarks>
///     Two-phase and dry-run first: a preview parses, guards and reports without writing a row, and the commit
///     replays that MATERIALISED payload rather than re-parsing or re-fetching, so the operator cannot approve one
///     payload and persist another. An imported skill always lands <see cref="AgentSkillRecord.Enabled" />
///     <c>false</c> with <see cref="AgentSkillOrigin.Imported" /> provenance, and a script is never imported, only
///     reported as refused. The pipeline and its guards: docs/wiki/04-agent-mode.md ("Import pipeline").
/// </remarks>
public interface ISkillImportService
{
    /// <summary>
    ///     Phase 1 for an uploaded <c>.zip</c>. Extracts in memory under the archive guards, discovers every
    ///     <c>SKILL.md</c>, and returns the report. Writes nothing.
    /// </summary>
    /// <exception cref="SkillImportException">An archive guard tripped, or the archive holds no skill.</exception>
    Task<SkillImportPreview> PreviewArchiveAsync(ReadOnlyMemory<byte> archive, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Phase 1 for an uploaded <c>.zip</c> that is still a stream: buffers it under
    ///     <see cref="SkillImportOptions.MaxArchiveBytes" />, then runs the same archive preview. Writes nothing.
    /// </summary>
    /// <remarks>
    ///     The cap applies to the bytes actually READ, never to a declared length, which is caller-controlled —
    ///     bounding what gets buffered is the whole point of the guard.
    /// </remarks>
    /// <exception cref="SkillImportException">
    ///     The upload exceeds the import size cap, an archive guard tripped, or the archive holds no skill.
    /// </exception>
    Task<SkillImportPreview> PreviewArchiveAsync(Stream archive, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Phase 1 for a pasted raw <c>SKILL.md</c>. A pasted document has no containing directory and no bundled
    ///     files, so the frontmatter <c>name</c> is authoritative and the skill imports instructions-only. Writes nothing.
    /// </summary>
    /// <exception cref="SkillImportException">The document carries no parsable frontmatter block.</exception>
    Task<SkillImportPreview> PreviewMarkdownAsync(string skillMarkdown, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Phase 1 for a GitHub repository. Writes nothing.
    /// </summary>
    /// <remarks>
    ///     <paramref name="owner" /> and <paramref name="repository" /> are the only caller-supplied parts of the
    ///     URL: a pasted URL is NEVER accepted, which is what keeps the host allowlist meaningful. A large collection
    ///     repository yields many candidates and the operator selects — nothing is ever bulk-imported.
    /// </remarks>
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
///     The import guard limits, bindable so an operator can tighten them without a rebuild.
/// </summary>
/// <remarks>
///     The defaults admit a real collection repository, well past a thousand entries and tens of megabytes, while
///     keeping the guards that actually bound memory tight. The ranking matters if you change them:
///     <see cref="MaxEntryBytes" /> and <see cref="MaxTotalInflatedBytes" /> bound what is INFLATED, and only entries
///     the import intends to keep are inflated at all, whereas <see cref="MaxEntries" /> bounds a cheap
///     central-directory walk and blocks ordinary repositories for almost nothing when set too low.
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
public sealed class SkillImportPreview
{
    /// <summary>Handle for <see cref="ISkillImportService.CommitAsync" />. Expires; consumed on a successful commit.</summary>
    public required Guid Token { get; init; }

    /// <summary>Provenance as it will be persisted: the literal <c>upload</c>, or <c>github:owner/repo</c>.</summary>
    public required string SourceUri { get; init; }

    /// <summary>Every skill discovered in the source, ordered by name.</summary>
    public required IReadOnlyList<SkillImportCandidate> Skills { get; init; }

    /// <summary>Source-level notes that block nothing (e.g. a frontmatter name that disagreed with its directory).</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
///     One discovered skill, exactly as it would be written. <see cref="Body" /> and <see cref="Resources" /> are
///     carried so the operator reviews the real content and so phase 2 has nothing left to re-derive.
/// </summary>
public sealed class SkillImportCandidate
{
    /// <summary>The skill name that will be persisted — the containing directory name when the source had one.</summary>
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string Body { get; init; }

    public required string? License { get; init; }

    public required string? Compatibility { get; init; }

    public required string? AllowedTools { get; init; }

    public required IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public required int BodySizeBytes { get; init; }

    public required int BodyLineCount { get; init; }

    public required IReadOnlyList<SkillImportResource> Resources { get; init; }

    /// <summary>Script files found and dropped. Listed because an operator should see what a skill expected to run.</summary>
    public required IReadOnlyList<string> RefusedScripts { get; init; }

    /// <summary>A skill with this name (NOCASE) is already in the library; the commit's conflict resolution decides.</summary>
    public required bool ConflictsWithExistingSkill { get; init; }

    /// <summary>Non-empty means this skill cannot be imported at all. Messages never echo untrusted content.</summary>
    public required IReadOnlyList<string> Problems { get; init; }

    /// <summary>True when nothing blocks this skill from being persisted.</summary>
    public bool CanImport => Problems.Count == 0;
}

/// <summary>One bundled file that passed the extension allowlist, the name charset guard and UTF-8 validation.</summary>
public sealed class SkillImportResource
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string MediaType { get; init; }

    public required string Content { get; init; }

    public required int SizeBytes { get; init; }
}

/// <summary>Phase 2 input: which skills from <see cref="Token" />'s report to write, and the operator's explicit consent.</summary>
public sealed class SkillImportCommitRequest
{
    public required Guid Token { get; init; }

    public required IReadOnlyList<string> SkillNames { get; init; }

    public SkillImportConflictResolution ConflictResolution { get; init; }

    /// <summary>
    ///     Must be <c>true</c>. An import that is not explicitly acknowledged fails and writes nothing — the operator is
    ///     confirming they read a preview of third-party instructions that will run with their agent's tool access.
    /// </summary>
    public bool Acknowledged { get; init; }
}

/// <summary>What to do when the library already holds a skill with the imported name.</summary>
public enum SkillImportConflictResolution
{
    /// <summary>Leave the existing skill untouched. The default: silently destroying operator content is the worst available outcome.</summary>
    Skip = 0,

    /// <summary>Overwrite the existing skill's content and resources. Loses local edits.</summary>
    Replace = 1
}

/// <summary>Per-skill outcome of a commit.</summary>
public sealed class SkillImportResult
{
    public required IReadOnlyList<SkillImportOutcome> Outcomes { get; init; }
}

/// <summary>What happened to one selected skill. <see cref="Reason" /> is operator-safe and echoes no imported content.</summary>
public sealed class SkillImportOutcome
{
    public required string Name { get; init; }

    public required SkillImportStatus Status { get; init; }

    public string? Reason { get; init; }
}

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
///     Thrown when an import is refused; every guard in the pipeline fails closed through it.
/// </summary>
/// <remarks>
///     Its message is written to be shown to the operator: it names the rule that was broken and NEVER echoes an
///     entry path, a resource name or any imported text, those being the injection sinks the guards exist to close.
/// </remarks>
public sealed class SkillImportException : Exception
{
    public SkillImportException(string message) : base(message)
    {
    }

    public SkillImportException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
