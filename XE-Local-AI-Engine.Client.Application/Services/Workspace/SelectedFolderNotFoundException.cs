namespace XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Thrown when a selected-folder id parses but no folder is registered under it. Derives from
///     <see cref="SelectedFolderValidationException" /> so the "folder resolution failed" catches in the service layer
///     keep treating it as one rejection; endpoints catch it first and map it to 404. The message never includes the
///     raw host path.
/// </summary>
public sealed class SelectedFolderNotFoundException : SelectedFolderValidationException
{
    public SelectedFolderNotFoundException()
    {
    }

    public SelectedFolderNotFoundException(string message) : base(message)
    {
    }

    public SelectedFolderNotFoundException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
