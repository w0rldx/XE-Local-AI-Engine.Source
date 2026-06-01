namespace XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Result of an operator cancel request against a scheduled job run. Cancellation is best-effort for cooperative
///     handlers, so the outcome distinguishes the cases the cancel endpoint must surface back to the caller.
/// </summary>
public enum RunCancellationOutcome
{
    /// <summary>No run exists with the supplied id.</summary>
    NotFound = 0,

    /// <summary>The run already reached a terminal state, so there is nothing to cancel.</summary>
    AlreadyTerminal = 1,

    /// <summary>
    ///     Cancellation was requested and the run was active in Quartz: its <c>CancellationToken</c> has been signalled.
    ///     The handler will move the run to <c>Cancelled</c> once it observes the token.
    /// </summary>
    Requested = 2,

    /// <summary>
    ///     Cancellation was requested (the timestamp is recorded) but Quartz reported no matching active fire — the run is
    ///     not currently executing in this process. Startup reconciliation will eventually mark such a run terminal.
    /// </summary>
    RequestedButNotRunning = 3
}
