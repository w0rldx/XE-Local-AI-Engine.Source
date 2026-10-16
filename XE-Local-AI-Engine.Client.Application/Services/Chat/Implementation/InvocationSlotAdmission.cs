namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Takes the node's one invocation slot for a chat turn and, while the turn is queued behind a turn in ANOTHER
///     conversation that is parked on an approval or an <c>ask_user</c> question, says so once with a
///     <see cref="TurnNoticeKind.QueuedBehindParkedTurn" /> notice.
/// </summary>
/// <remarks>
///     A parked turn keeps the slot until its card is answered or expires (the pending-tool-call age, ten minutes by
///     default), and the queued turn could not tell why it was waiting. Releasing the slot while parked is the real fix
///     but needs per-invocation state in <c>WorkerEventDispatcher</c> and <c>InvocationLifecycleTracker</c>, which both
///     track exactly one current invocation today; until then the notice names the conversation that holds the slot.
/// </remarks>
internal static class InvocationSlotAdmission
{
    /// <summary>Sanitized notice text; the SPA localizes it by this exact text.</summary>
    internal const string QueuedBehindParkedTurnMessage =
        "This message is waiting for an approval or a question in another conversation. Answer it there to continue.";

    public static async Task<IAsyncDisposable> AcquireAsync(IWorkerEventDispatcher dispatcher,
        RuntimePackage package,
        Guid requestId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var acquire = dispatcher.ReportInvocationAssignedAsync(package, cancellationToken);
        if (acquire.IsCompleted)
        {
            return await acquire;
        }

        var notified = 0;
        Task? notice = null;

        // A notice is advisory: a throwing subscriber must never cost the turn its slot, so nothing here may escape.
        void Observe(InvocationState? holder)
        {
            try
            {
                if (holder is not null
                    && (holder.PendingApproval is not null || holder.PendingQuestion is not null)
                    && holder.ConversationId != package.ConversationId
                    && !NodeChatInvocationPump.IsTerminal(holder.Status)
                    && Interlocked.Exchange(ref notified, 1) == 0)
                {
                    notice = dispatcher.ReportTurnNoticeAsync(new TurnNoticePayload
                    {
                        InvocationId = requestId,
                        Kind = TurnNoticeKind.QueuedBehindParkedTurn,
                        Message = QueuedBehindParkedTurnMessage,
                        Detail = holder.ConversationId.ToString()
                    });
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "The queued-behind-a-parked-turn notice for request {RequestId} could not be reported.", requestId);
            }
        }

        void OnStateChanged(object? sender, InvocationStateChangedEventArgs args) => Observe(args.State);

        dispatcher.InvocationStateChanged += OnStateChanged;
        try
        {
            // The holder may have parked before this turn queued, so read the current state once as well.
            Observe(dispatcher.CurrentInvocation);
            return await acquire;
        }
        finally
        {
            dispatcher.InvocationStateChanged -= OnStateChanged;
            if (notice is not null)
            {
                try
                {
                    await notice;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "The queued-behind-a-parked-turn notice for request {RequestId} could not be reported.", requestId);
                }
            }
        }
    }
}
