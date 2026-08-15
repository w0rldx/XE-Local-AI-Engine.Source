namespace XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Thrown when a selected-folder registration collides with one that already exists (the normalized alias is taken).
///     Derives from <see cref="SelectedFolderValidationException" /> so the "folder resolution failed" catches in the
///     service layer keep treating it as one rejection; endpoints catch it first and map it to 409. The message never
///     includes the raw host path.
/// </summary>
public sealed class SelectedFolderConflictException : SelectedFolderValidationException
{
    public SelectedFolderConflictException()
    {
    }

    public SelectedFolderConflictException(string message) : base(message)
    {
    }

    public SelectedFolderConflictException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
