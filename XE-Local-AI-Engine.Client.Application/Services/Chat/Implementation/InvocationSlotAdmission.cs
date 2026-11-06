namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>Takes the node's one invocation slot for a chat turn, queueing behind the turn that holds it.</summary>
/// <remarks>
///     A turn parked on an approval or an <c>ask_user</c> question gives the slot back (<c>HumanPark</c>), so a queued turn only ever
///     waits behind a running one and needs no notice naming a parked holder. <see cref="TurnNoticeKind.QueuedBehindParkedTurn" /> is no
///     longer emitted; persisted rows keep rendering it.
/// </remarks>
internal static class InvocationSlotAdmission
{
    /// <summary>Refuses a turn into a conversation that has a live turn off the slot: parked on a card, or answered and waiting to resume.</summary>
    public static void EnsureNoParkedTurn(IWorkerEventDispatcher dispatcher, Guid conversationId)
    {
        var snapshot = dispatcher.GetLiveSnapshot();
        var holderId = snapshot.Current is { } current && snapshot.Live.Any(state => state.InvocationId == current.InvocationId)
            ? current.InvocationId
            : (Guid?)null;
        if (snapshot.Live.Any(state => state.ConversationId == conversationId
                                       && (state.PendingApproval is not null || state.PendingQuestion is not null || state.InvocationId != holderId)))
        {
            throw new NodeChatTurnParkedException(conversationId);
        }
    }

    /// <summary>Refuses a turn into a conversation whose bound graph-workflow run is live, parked runs included: a normal turn would interleave with it.</summary>
    /// <remarks>Only the newest run can be live; the database allows one per conversation. The SPA gate is cosmetic beside this.</remarks>
    public static async Task EnsureNoLiveWorkflowRunAsync(IGraphWorkflowStore graphWorkflows, Guid conversationId, CancellationToken cancellationToken)
    {
        var runs = await graphWorkflows.ListRunsByConversationAsync(conversationId, limit: 1, cancellationToken);
        if (runs.Count > 0 && !GraphWorkflowStateMachine.IsTerminal(runs[0].Status))
        {
            throw new NodeChatWorkflowRunLiveException(conversationId);
        }
    }

    public static Task<IAsyncDisposable> AcquireAsync(IWorkerEventDispatcher dispatcher, RuntimePackage package, CancellationToken cancellationToken) =>
        dispatcher.ReportInvocationAssignedAsync(package, cancellationToken);
}
