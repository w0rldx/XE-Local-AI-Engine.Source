namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;

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
    ///     remote run. Returns a session whose correlation drives subsequent pump flushes/terminalization, or
    ///     <see langword="null" /> when the assistant row reached a terminal status before it could be marked streaming
    ///     (e.g. an early cancel): there is nothing to persist into, so the caller runs the invocation without a
    ///     node-local mirror rather than opening a session against a terminal row.
    /// </summary>
    Task<NodeChatRemotePersistenceSession?> BeginAsync(RuntimePackage package, CancellationToken cancellationToken = default);
}
