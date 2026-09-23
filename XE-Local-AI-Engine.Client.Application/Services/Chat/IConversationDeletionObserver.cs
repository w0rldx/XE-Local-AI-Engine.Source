namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Told about a conversation delete BEFORE its rows go, so a feature that binds work to a conversation can wind it
///     down first. Chat knows nothing about who listens; each feature registers its own observer.
/// </summary>
public interface IConversationDeletionObserver
{
    /// <param name="purge">True when the rows are deleted now; false for a soft delete, which keeps them.</param>
    Task OnDeletingAsync(Guid conversationId, bool purge, CancellationToken cancellationToken);
}
