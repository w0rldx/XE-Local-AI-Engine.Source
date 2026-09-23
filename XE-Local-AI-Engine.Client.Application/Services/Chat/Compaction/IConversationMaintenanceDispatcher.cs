namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction;

/// <summary>Queues post-turn conversation maintenance (automatic compaction) onto the background worker.</summary>
public interface IConversationMaintenanceDispatcher
{
    /// <summary>
    ///     Queues <paramref name="job" /> without blocking or throwing into the caller.
    /// </summary>
    /// <remarks>
    ///     A job for a conversation and kind that is already queued or running is dropped, as is any job once the
    ///     bounded queue is full or the worker has stopped.
    /// </remarks>
    void Dispatch(ConversationMaintenanceJob job);
}
