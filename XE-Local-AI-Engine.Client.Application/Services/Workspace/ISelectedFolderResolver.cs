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
    ///     persists the folder. Throws <see cref="SelectedFolderConflictException" /> when the normalized alias is
    ///     already registered and <see cref="SelectedFolderValidationException" /> for any other rejection.
    /// </summary>
    Task<SelectedFolderReference> RegisterAsync(SelectedFolderRegistration registration, CancellationToken cancellationToken = default);

    /// <summary>Lists registered folders as model-facing references (id + alias only).</summary>
    Task<IReadOnlyList<SelectedFolderReference>> ListReferencesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Resolves a folder's opaque id OR its human-facing alias to its trusted host path for worker-internal use. A GUID is matched
    ///     first, so an alias that merely looks like one can never shadow a real id.
    /// </summary>
    /// <remarks>
    ///     Throws <see cref="SelectedFolderValidationException" /> for a value that is neither a GUID nor a well-formed alias, and
    ///     <see cref="SelectedFolderNotFoundException" /> (which derives from it, so a caller catching the base type handles both) for a
    ///     well-formed value that matches no active folder.
    /// </remarks>
    Task<ResolvedSelectedFolder> ResolveAsync(string id, CancellationToken cancellationToken = default);
}
