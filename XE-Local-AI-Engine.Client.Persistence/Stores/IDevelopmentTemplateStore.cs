namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     A registered template. <see cref="HostPath" /> is trusted-side only and must never cross the API boundary — the
///     Development contracts project templates as id plus alias, exactly as they do selected folders.
/// </summary>
public sealed record DevelopmentTemplateSnapshot(
    Guid Id,
    string Alias,
    string HostPath,
    long CreatedAtUtc,
    long Version);

/// <summary>Where a materialized repository came from (S2.2). <see cref="TemplatePath" /> is trusted-side only.</summary>
public sealed record DevelopmentTemplateMaterializationSnapshot(
    Guid SelectedFolderId,
    Guid TemplateId,
    string TemplateAlias,
    string TemplatePath,
    string TemplateCommit,
    long CreatedAtUtc);

/// <summary>
///     The template registry and the provenance of repositories materialized from it.
///     <para>
///         Deliberately separate from <see cref="IDevelopmentStore" />: templates are node-scoped configuration with no
///         project, task, attempt, operation key or event stream, so folding them into the operation-journalled
///         Development store would give them a transactional shape they do not need.
///     </para>
/// </summary>
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
