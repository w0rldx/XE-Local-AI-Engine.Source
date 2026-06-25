namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

/// <summary>Create request for a skill. The editable fields mirror <see cref="AgentSkillInput" /> (no Enabled — a new skill defaults to enabled).</summary>
public sealed class CreateSkillRequest
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? Body { get; init; }
}

/// <summary>Update request for a skill. The id travels in the route; the body carries the new field values plus the library-wide Enabled toggle.</summary>
public sealed class UpdateSkillRequest
{
    public Guid SkillId { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? Body { get; init; }

    public bool Enabled { get; init; } = true;
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
}

/// <summary>
///     List projection of a stored skill. Deliberately omits <see cref="SkillResponse.Body" /> — bodies can be large
///     and are fetched per-id when the editor opens. The model never sees this DTO; it carries only the metadata the
///     library list needs.
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
}

public sealed class ListSkillsResponse
{
    public required IReadOnlyList<SkillSummaryResponse> Items { get; init; }
}
