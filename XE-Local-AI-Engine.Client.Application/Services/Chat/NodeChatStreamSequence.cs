namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Monotonic, thread-safe sequence source for a single chat stream, shared and atomic because the local front
///     door emits ordered events from two concurrent producers and client-side ordering depends on it.
/// </summary>
public sealed class NodeChatStreamSequence
{
    private long _next = -1;

    /// <summary>Returns the next sequence number, starting at 0.</summary>
    public long Next()
    {
        return Interlocked.Increment(ref _next);
    }
}
