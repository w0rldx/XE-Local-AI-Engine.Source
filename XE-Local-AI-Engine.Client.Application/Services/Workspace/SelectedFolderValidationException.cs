namespace XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Thrown when a selected-folder registration or resolution is rejected — invalid/colliding alias, a non-absolute
///     or traversal-bearing host path, or an unknown id. Distinct from infrastructure failures so callers (and tests)
///     can map it to a model-facing rejection. The message never includes the raw host path.
/// </summary>
public sealed class SelectedFolderValidationException : InvalidOperationException
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
