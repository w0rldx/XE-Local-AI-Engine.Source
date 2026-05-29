namespace XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Safe resolver over the node selected-folder store. Owns alias normalization, host-path validation, and the
///     "model sees only id + alias" contract. The model never receives a raw host path; only the worker resolves an id
///     to a trusted <see cref="ResolvedSelectedFolder" />.
/// </summary>
public interface ISelectedFolderResolver
{
    /// <summary>
    ///     Normalizes the alias, validates the host path (absolute, traversal-free), rejects alias collisions, then
    ///     persists the folder. Throws <see cref="SelectedFolderValidationException" /> on any rejection.
    /// </summary>
    Task<SelectedFolderReference> RegisterAsync(SelectedFolderRegistration registration, CancellationToken cancellationToken = default);

    /// <summary>Lists registered folders as model-facing references (id + alias only).</summary>
    Task<IReadOnlyList<SelectedFolderReference>> ListReferencesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Resolves an opaque folder id to its trusted host path for worker-internal use. Throws
    ///     <see cref="SelectedFolderValidationException" /> for an unparsable or unknown id.
    /// </summary>
    Task<ResolvedSelectedFolder> ResolveAsync(string id, CancellationToken cancellationToken = default);
}
