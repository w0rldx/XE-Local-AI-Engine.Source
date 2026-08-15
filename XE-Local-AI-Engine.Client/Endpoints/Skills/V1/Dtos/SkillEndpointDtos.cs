namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>Create request for a skill. The editable fields mirror <see cref="AgentSkillInput" /> (no Enabled — a new skill defaults to enabled).</summary>
public sealed class CreateSkillRequest
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? Body { get; init; }

    public string? License { get; init; }

    public string? Compatibility { get; init; }

    /// <summary>The spec's space-delimited tool list. Kept as one string — MAF consumes it in that form.</summary>
    public string? AllowedTools { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    ///     Set by the client when this content came from an applied AI draft. It forces the Imported posture server-side
    ///     — <c>Origin=Imported</c>, <c>Enabled=false</c>, <c>SourceUri="generated"</c> — so model-written instructions
    ///     land in the same fenced, review-first bucket as any other third-party skill.
    /// </summary>
    public bool Generated { get; init; }

    /// <summary>The draft response's provenance block, echoed back unchanged. Optional; informational (see the type).</summary>
    public GenerationMetadata? GenerationMetadata { get; init; }
}

/// <summary>
///     Update request for a skill. The id travels in the route; the body carries the new field values plus the
///     library-wide Enabled toggle.
///     <para>
///         The frontmatter fields are on this request because the store writes the frontmatter column from the input
///         unconditionally: an update that did not carry them back would silently erase an imported skill's
///         <c>license</c> / <c>allowed-tools</c> / <c>metadata</c> the first time an operator saved an unrelated edit.
///         This is a full replacement, as PUT implies — an omitted field clears the stored value.
///     </para>
///     <para>
///         <b>Two documented exceptions to that full-replacement rule</b>, both mirroring the store's promote-only
///         provenance: an omitted <see cref="GenerationMetadata" /> preserves the stored provenance rather than
///         clearing it, and <see cref="Generated" /> can only tighten posture, never loosen it.
///     </para>
/// </summary>
public sealed class UpdateSkillRequest
{
    public Guid SkillId { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? Body { get; init; }

    public bool Enabled { get; init; } = true;

    public string? License { get; init; }

    public string? Compatibility { get; init; }

    public string? AllowedTools { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    ///     Set by the client when the submitted content came from an applied AI draft — including an AI <em>improve</em>
    ///     of an existing skill. It forces the Imported posture server-side (<c>Origin=Imported</c>,
    ///     <c>Enabled=false</c>, <c>SourceUri="generated"</c>) from ANY prior state, overriding
    ///     <see cref="Enabled" />: model-revised content is no more trusted than model-written content, so an improve
    ///     cannot be used to launder instructions into an already-enabled local skill.
    /// </summary>
    public bool Generated { get; init; }

    /// <summary>
    ///     The draft response's provenance block, echoed back unchanged. Optional, and <b>set-if-present</b>: omitting
    ///     it leaves any stored provenance alone rather than clearing it, so an ordinary edit cannot erase the record of
    ///     how the skill was originally drafted.
    /// </summary>
    public GenerationMetadata? GenerationMetadata { get; init; }
}

public sealed class GetSkillRequest
{
    public Guid SkillId { get; init; }
}

public sealed class DeleteSkillRequest
{
    public Guid SkillId { get; init; }
}

/// <summary>
///     Full wire projection of a stored skill, including the decrypted markdown <see cref="Body" />. Returned by the
///     create/get/update endpoints; the list endpoint omits the body for payload economy.
///     <para>
///         The optional frontmatter fields (<see cref="License" />, <see cref="Compatibility" />,
///         <see cref="AllowedTools" />, <see cref="Metadata" />) are the spec's own keys, carried verbatim.
///         <see cref="Origin" />, <see cref="SourceUri" /> and <see cref="ImportedAtUtc" /> are the provenance the
///         "Imported" badge renders — third-party content is fenced at runtime and denied session-scoped approval, so
///         an operator has to be able to see which rows those are.
///     </para>
/// </summary>
public sealed class SkillResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string Body { get; init; }

    public required bool Enabled { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public string? License { get; init; }

    public string? Compatibility { get; init; }

    /// <summary>The spec's space-delimited tool list, kept as the single string MAF consumes rather than split.</summary>
    public string? AllowedTools { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public required AgentSkillOrigin Origin { get; init; }

    /// <summary>Provenance as persisted: the literal <c>upload</c>, or <c>github:owner/repo</c>. Null for operator-authored skills.</summary>
    public string? SourceUri { get; init; }

    public long? ImportedAtUtc { get; init; }

    /// <summary>
    ///     AI-drafting provenance when this skill came from a draft, otherwise null. Carried on this full projection
    ///     only — <see cref="SkillSummaryResponse" /> deliberately omits it so the library list stays lean.
    /// </summary>
    public GenerationMetadataResponse? GenerationMetadata { get; init; }

    /// <summary>Bundled files this skill carries. Contents are fetched per resource; see the resources routes.</summary>
    public required int ResourceCount { get; init; }
}

/// <summary>
///     List projection of a stored skill. Deliberately omits <see cref="SkillResponse.Body" /> — bodies can be large
///     and are fetched per-id when the editor opens. The model never sees this DTO; it carries only the metadata the
///     library list needs.
///     <para>
///         It also omits <see cref="SkillResponse.ResourceCount" />: the list query does not load resources (decrypting
///         every bundled file of every skill to render a list would be pure waste), so any count here would be a
///         constant zero dressed up as data. The per-skill GET carries the real number.
///     </para>
/// </summary>
public sealed class SkillSummaryResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required bool Enabled { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public string? License { get; init; }

    public string? Compatibility { get; init; }

    public string? AllowedTools { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public required AgentSkillOrigin Origin { get; init; }

    public string? SourceUri { get; init; }

    public long? ImportedAtUtc { get; init; }
}

public sealed class ListSkillsResponse
{
    public required IReadOnlyList<SkillSummaryResponse> Items { get; init; }
}

/// <summary>Route binding for <c>GET skills/{skillId}/resources</c>.</summary>
public sealed class ListSkillResourcesRequest
{
    public Guid SkillId { get; init; }
}

/// <summary>
///     Route binding for <c>GET skills/{skillId}/resources/{resourceName}</c>. <see cref="ResourceName" /> arrives
///     percent-escaped whenever it carries a slash, so the endpoint decodes and validates it before the lookup.
/// </summary>
public sealed class GetSkillResourceRequest
{
    public Guid SkillId { get; init; }

    public string? ResourceName { get; init; }
}

/// <summary>
///     One bundled file as the library list sees it. Carries no content: a resource is up to a megabyte of
///     third-party text and the list only needs to name what exists.
/// </summary>
public sealed class SkillResourceSummaryResponse
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string MediaType { get; init; }

    public required int SizeBytes { get; init; }
}

public sealed class ListSkillResourcesResponse
{
    public required IReadOnlyList<SkillResourceSummaryResponse> Items { get; init; }
}

/// <summary>One bundled file including its decrypted content. Fetched per resource, never in bulk.</summary>
public sealed class SkillResourceResponse
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string MediaType { get; init; }

    public required int SizeBytes { get; init; }

    public required string Content { get; init; }
}
