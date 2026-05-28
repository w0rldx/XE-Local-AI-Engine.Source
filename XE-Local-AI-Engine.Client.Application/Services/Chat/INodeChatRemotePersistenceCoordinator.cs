namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Persists the chat content of a PLATFORM-served (Origin=Remote) invocation to node SQLite, mirroring what the
///     local front door does for loopback chat but persistence-only (no SSE response). It ensures the local
///     conversation row exists, synthesizes + persists the user turn and assistant placeholder, then returns a
///     session the caller drives with the run's <see cref="Events.InvocationState" /> deltas through the shared
///     <see cref="INodeChatInvocationPump" />.
/// </summary>
/// <remarks>
///     Keeps <c>WorkerEventDispatcher</c> thin: the dispatcher only opens a session around a run and feeds it states;
///     all persistence translation lives here and in the shared pump. Origin=Remote rows are node-local only and
///     never sync back (RC).
/// </remarks>
public interface INodeChatRemotePersistenceCoordinator
{
    /// <summary>
    ///     Ensures the conversation, persists the synthesized user turn, and creates the assistant placeholder for a
    ///     remote run. Returns a session whose correlation drives subsequent pump flushes/terminalization.
    /// </summary>
    Task<NodeChatRemotePersistenceSession> BeginAsync(RuntimePackage package, CancellationToken cancellationToken = default);
}
