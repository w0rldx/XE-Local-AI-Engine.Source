namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Says why a chat turn is queued when a work-session step holds the node's one invocation slot, with a
///     <see cref="TurnNoticeKind.QueuedBehindWorkSession" /> notice.
/// </summary>
/// <remarks>
///     A step keeps the slot for its whole turn, so an operator's chat turn waits for the next step boundary. A step
///     parked on an approval or a question gives the slot back, so it is never the holder this reads.
/// </remarks>
internal static class WorkSessionSlotHolderNotice
{
    /// <summary>Sanitized notice text, worded to read right live and on a reloaded reply; the SPA localizes it by this text.</summary>
    internal const string Message = "This message was queued behind a running work session step.";

    /// <summary>Reports the notice when the slot holder is a live turn in another conversation that a work session owns.</summary>
    public static async Task ReportIfQueuedBehindAsync(IWorkerEventDispatcher dispatcher,
        INodeChatPersistenceService persistence,
        Guid conversationId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        if (dispatcher.SlotHolder is not { } holder
            || holder.ConversationId == conversationId
            || NodeChatInvocationPump.IsTerminal(holder.Status)
            || !string.Equals(await persistence.GetConversationKindAsync(holder.ConversationId, cancellationToken),
                NodeConversationKind.WorkSession,
                StringComparison.Ordinal))
        {
            return;
        }

        await dispatcher.ReportTurnNoticeAsync(new TurnNoticePayload
        {
            InvocationId = requestId,
            Kind = TurnNoticeKind.QueuedBehindWorkSession,
            Message = Message,
            Detail = holder.ConversationId.ToString()
        });
    }
}
