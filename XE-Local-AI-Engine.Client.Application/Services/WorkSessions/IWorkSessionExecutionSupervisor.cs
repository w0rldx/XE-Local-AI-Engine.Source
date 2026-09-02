namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>Why a running session is being stopped, which decides the status it lands on.</summary>
internal enum WorkSessionStopReason
{
    Pause,
    Cancel
}

/// <summary>
///     Drives work sessions as a detached sequence of steps, off the HTTP and SignalR request paths.
///     <para>
///         <c>MaxConcurrentSessions</c> is an ADMISSION cap, not a concurrency setting. The node has exactly one
///         invocation slot — <c>WorkerEventDispatcher</c> holds a <c>SemaphoreSlim(1, 1)</c> that every invocation takes
///         — so a second admitted session buys queue depth, not parallelism, and a running step delays the operator's
///         own chat turn until it finishes. That is a node-wide behavioural consequence of shipping work sessions, and
///         <c>MaxParkedSeconds</c> is what bounds its worst case.
///     </para>
/// </summary>
internal interface IWorkSessionExecutionSupervisor
{
    /// <summary>
    ///     Admits a session and starts driving it. Returns <see langword="false" /> when the feature is off, the session
    ///     is already in flight here, or the admission cap is full.
    ///     <para>
    ///         <paramref name="runtime" /> pins what the steps of THIS run use instead of the bound agent's own model
    ///         and effort, and is held for the run rather than stored — the caller supplies it again on the next start
    ///         or resume, so nothing here has to survive a restart.
    ///     </para>
    /// </summary>
    bool TryStart(Guid sessionId, WorkSessionRuntimeOverride? runtime = null);

    /// <summary>
    ///     Whether another session could be admitted right now. Read BEFORE a caller moves a session to <c>Running</c>,
    ///     so a full node refuses without first writing a status nothing is driving.
    /// </summary>
    bool HasCapacity { get; }

    /// <summary>
    ///     Cancels the in-flight step and stops the loop, then waits briefly for it to land on its terminal status.
    ///     Returns <see langword="false" /> when this supervisor is not driving that session.
    /// </summary>
    ValueTask<bool> TryStopAsync(Guid sessionId, WorkSessionStopReason reason, CancellationToken cancellationToken = default);

    bool IsRunning(Guid sessionId);
}
