namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     A registered template. <see cref="HostPath" /> is trusted-side only and must never cross the API boundary — the
///     Development contracts project templates as id plus alias, exactly as they do selected folders.
/// </summary>
public sealed class DevelopmentTemplateSnapshot
{
    public required Guid Id { get; init; }

    public required string Alias { get; init; }

    public required string HostPath { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long Version { get; init; }
}

/// <summary>Where a materialized repository came from. <see cref="TemplatePath" /> is trusted-side only.</summary>
public sealed class DevelopmentTemplateMaterializationSnapshot
{
    public required Guid SelectedFolderId { get; init; }

    public required Guid TemplateId { get; init; }

    public required string TemplateAlias { get; init; }

    public required string TemplatePath { get; init; }

    public required string TemplateCommit { get; init; }

    public required long CreatedAtUtc { get; init; }
}

/// <summary>
///     The template registry and the provenance of repositories materialized from it.
/// </summary>
/// <remarks>
///     Deliberately separate from <see cref="IDevelopmentStore" />: templates are node-scoped configuration with no
///     project, task, attempt, operation key or event stream, so folding them into the operation-journalled
///     Development store would give them a transactional shape they do not need.
/// </remarks>
public interface IDevelopmentTemplateStore
{
    Task<IReadOnlyList<DevelopmentTemplateSnapshot>> ListAsync(CancellationToken cancellationToken = default);

    Task<DevelopmentTemplateSnapshot> GetAsync(Guid templateId, CancellationToken cancellationToken = default);

    Task<DevelopmentTemplateSnapshot> AddAsync(string templateAlias, string hostPath, CancellationToken cancellationToken = default);

    /// <summary>Removes a template. Repositories already created from it keep their provenance.</summary>
    Task<bool> RemoveAsync(Guid templateId, CancellationToken cancellationToken = default);

    Task RecordMaterializationAsync(DevelopmentTemplateMaterializationSnapshot materialization,
        CancellationToken cancellationToken = default);

    Task<DevelopmentTemplateMaterializationSnapshot?> FindMaterializationAsync(Guid selectedFolderId,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Thrown when a template alias is already taken. The unique index is the authority — a pre-check would still race
///     two concurrent adds — so this is raised from the constraint violation rather than from a lookup.
/// </summary>
public sealed class DevelopmentTemplateAliasInUseException : InvalidOperationException
{
    public DevelopmentTemplateAliasInUseException(string message, Exception innerException) : base(message, innerException) { }

    public DevelopmentTemplateAliasInUseException(string message) : base(message) { }

    public DevelopmentTemplateAliasInUseException() { }
}
