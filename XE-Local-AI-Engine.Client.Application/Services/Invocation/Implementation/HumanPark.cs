namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>One human park of a turn: it gives the node's invocation slot back while it waits, and takes it again before the model runs.</summary>
/// <remarks>
///     <see cref="ResumeAsync" /> runs on every path where the turn calls the model again: an answer, a deny, a web review and a timed-out
///     question. An expired approval and a cancelled turn only <see cref="End" />, so they terminalize without the slot. The reacquire comes
///     BEFORE the deadline re-arm, so the parked backstop still bounds the wait for the slot.
/// </remarks>
public sealed class HumanPark
{
    /// <summary>Sanitized notice text, persisted with the reply; the SPA localizes it by this exact text.</summary>
    internal const string ResumeQueuedMessage = "After your answer, this reply was queued behind another running reply.";

    private readonly IWorkerEventDispatcher _dispatcher;
    private readonly Guid _invocationId;
    private readonly ILogger? _logger;
    private readonly InvocationLifecycleTracker _tracker;
    private bool _parked;
    private bool _slotReleased;

    public HumanPark(InvocationLifecycleTracker tracker, IWorkerEventDispatcher dispatcher, Guid invocationId, ILogger? logger = null)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _invocationId = invocationId;
        _logger = logger;
    }

    public void Park()
    {
        _tracker.SetInvocationDeadline(_invocationId, parkedOnHuman: true);
        _parked = true;

        // False for a run that never held the slot (no lease was taken for it), which then has nothing to take back.
        _slotReleased = _dispatcher.ReleaseSlotForPark(_invocationId);
    }

    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        if (!_parked)
        {
            return;
        }

        if (_slotReleased)
        {
            var reacquire = _dispatcher.ReacquireSlotAsync(_invocationId, cancellationToken);
            if (!reacquire.IsCompleted)
            {
                await ReportQueuedAsync();
            }

            await reacquire;
            _slotReleased = false;
        }

        End();
    }

    // Advisory: a throwing subscriber must never cost the turn its resume, so nothing here may escape.
    private async Task ReportQueuedAsync()
    {
        try
        {
            await _dispatcher.ReportTurnNoticeAsync(new TurnNoticePayload
            {
                InvocationId = _invocationId,
                Kind = TurnNoticeKind.ResumeQueuedBehindTurn,
                Message = ResumeQueuedMessage,
                Detail = _dispatcher.SlotHolder?.ConversationId.ToString()
            });
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "The resume-queued notice for invocation {InvocationId} could not be reported.", _invocationId);
        }
    }

    /// <summary>Ends the park without taking the slot back. Idempotent, so a <c>finally</c> may always call it.</summary>
    public void End()
    {
        if (!_parked)
        {
            return;
        }

        _parked = false;
        _tracker.SetInvocationDeadline(_invocationId, parkedOnHuman: false);
    }
}
