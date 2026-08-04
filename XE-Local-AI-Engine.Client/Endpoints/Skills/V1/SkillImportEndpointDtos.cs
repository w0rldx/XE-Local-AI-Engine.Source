namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>Which of the three import sources a preview request carries.</summary>
public enum SkillImportSourceKind
{
    /// <summary>A <c>.zip</c> on the multipart form.</summary>
    Upload = 0,

    /// <summary>A pasted raw <c>SKILL.md</c> document.</summary>
    Paste = 1,

    /// <summary>An owner/repository pair. A pasted URL is never accepted — that is what keeps the host allowlist meaningful.</summary>
    GitHub = 2
}

/// <summary>
///     Phase 1 request. The body is <c>multipart/form-data</c> for all three sources, not just the upload: one content
///     type means one binding path and one code path, and the two text sources cost a form field each.
///     <see cref="Source" /> names which payload is authoritative, so an upload that also carries pasted text can never
///     be resolved ambiguously.
/// </summary>
public sealed class SkillImportPreviewRequest
{
    public SkillImportSourceKind Source { get; init; }

    /// <summary>The archive, for <see cref="SkillImportSourceKind.Upload" />. Bounded by the configured archive cap.</summary>
    public IFormFile? File { get; init; }

    /// <summary>The raw <c>SKILL.md</c> document, for <see cref="SkillImportSourceKind.Paste" />.</summary>
    public string? Markdown { get; init; }

    /// <summary>Repository owner, for <see cref="SkillImportSourceKind.GitHub" />. Charset-validated by the import service.</summary>
    public string? Owner { get; init; }

    /// <summary>Repository name, for <see cref="SkillImportSourceKind.GitHub" />. Charset-validated by the import service.</summary>
    public string? Repository { get; init; }
}

/// <summary>
///     The dry-run report the operator approves. Nothing was written to produce it. <see cref="Token" /> is the
///     short-lived, single-use handle to the materialised payload behind this report — phase 2 persists that payload
///     verbatim rather than re-parsing the upload or re-fetching the repository.
/// </summary>
public sealed class SkillImportPreviewResponse
{
    public required Guid Token { get; init; }

    /// <summary>Provenance as it will be persisted: the literal <c>upload</c>, or <c>github:owner/repo</c>.</summary>
    public required string SourceUri { get; init; }

    public required IReadOnlyList<SkillImportCandidateResponse> Skills { get; init; }

    /// <summary>Source-level notes that block nothing.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
///     One discovered skill, exactly as it would be written. The <see cref="Body" /> is carried because reviewing the
///     real instructions is the whole point of a preview; resource <em>contents</em> are not — the operator reviews
///     what bundled files exist, and a skill may carry dozens of them.
/// </summary>
public sealed class SkillImportCandidateResponse
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string Body { get; init; }

    public string? License { get; init; }

    public string? Compatibility { get; init; }

    public string? AllowedTools { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Body size and line count, so the UI can flag a body past the spec's "&lt;500 lines" guidance.</summary>
    public required int BodySizeBytes { get; init; }

    public required int BodyLineCount { get; init; }

    public required IReadOnlyList<SkillResourceSummaryResponse> Resources { get; init; }

    /// <summary>Script files found and dropped. Listed because an operator should see what a skill expected to run.</summary>
    public required IReadOnlyList<string> RefusedScripts { get; init; }

    public required bool ConflictsWithExistingSkill { get; init; }

    /// <summary>Non-empty means this skill cannot be imported at all. Messages never echo untrusted content.</summary>
    public required IReadOnlyList<string> Problems { get; init; }

    public required bool CanImport { get; init; }
}

/// <summary>Phase 2 request: which skills from the approved report to write, and the operator's explicit consent.</summary>
public sealed class SkillImportCommitEndpointRequest
{
    public Guid Token { get; init; }

    public IReadOnlyList<string>? SkillNames { get; init; }

    /// <summary>Defaults to <see cref="SkillImportConflictResolution.Skip" /> — silently destroying operator content is the worst available default.</summary>
    public SkillImportConflictResolution ConflictResolution { get; init; } = SkillImportConflictResolution.Skip;

    /// <summary>Must be <c>true</c>. An unacknowledged import fails and writes nothing.</summary>
    public bool Acknowledged { get; init; }
}

public sealed class SkillImportCommitResponse
{
    public required IReadOnlyList<SkillImportOutcomeResponse> Outcomes { get; init; }
}

/// <summary>What happened to one selected skill. <see cref="Reason" /> is operator-safe and echoes no imported content.</summary>
public sealed class SkillImportOutcomeResponse
{
    public required string Name { get; init; }

    public required SkillImportStatus Status { get; init; }

    public string? Reason { get; init; }
}
