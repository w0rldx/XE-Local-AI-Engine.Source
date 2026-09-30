namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

/// <summary>
///     The six specification frontmatter keys plus the body.
/// </summary>
/// <remarks>
///     <see cref="AllowedTools" /> is normalised here, once, to the space-delimited string form MAF consumes and
///     persistence stores, so no caller downstream need know the frontmatter could also have written a sequence.
/// </remarks>
internal sealed class SkillFrontmatterDocument
{
    public required string? Name { get; init; }

    public required string? Description { get; init; }

    public required string? License { get; init; }

    public required string? Compatibility { get; init; }

    public required string? AllowedTools { get; init; }

    public required IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public required string Body { get; init; }
}
