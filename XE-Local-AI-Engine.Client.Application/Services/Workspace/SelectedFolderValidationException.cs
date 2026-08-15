namespace XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Thrown when a selected-folder registration or resolution is rejected because the input itself is bad — an
///     unusable alias, a non-absolute or traversal-bearing host path, an unparsable id, or a workspace that is no
///     longer active. Distinct from infrastructure failures so callers (and tests) can map it to a model-facing
///     rejection; endpoints map it to 400. The two shape-specific rejections derive from it —
///     <see cref="SelectedFolderNotFoundException" /> (unknown id → 404) and
///     <see cref="SelectedFolderConflictException" /> (alias already registered → 409) — so service-layer catches that
///     only care that folder resolution failed keep working unchanged, while endpoints catch the derived types first.
///     The message never includes the raw host path.
/// </summary>
public class SelectedFolderValidationException : InvalidOperationException
{
    public SelectedFolderValidationException()
    {
    }

    public SelectedFolderValidationException(string message) : base(message)
    {
    }

    public SelectedFolderValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
