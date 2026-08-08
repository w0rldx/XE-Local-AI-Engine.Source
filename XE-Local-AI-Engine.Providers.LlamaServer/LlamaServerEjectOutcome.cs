namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     The result of an operator eject request against a supervised <c>(model, role)</c> process. An eject is graceful
///     by default: it waits a bounded window for in-flight inference to drain before tearing the process down, and
///     reports honestly when it could not complete safely rather than killing a running turn silently.
/// </summary>
public enum LlamaServerEjectOutcome
{
    /// <summary>No process was running for the <c>(model, role)</c>. The eject is an idempotent no-op.</summary>
    NotRunning = 0,

    /// <summary>
    ///     The process was idle (or its in-flight work drained within the bounded window) and was torn down cleanly. No
    ///     running turn was interrupted.
    /// </summary>
    Ejected = 1,

    /// <summary>
    ///     In-flight inference did not finish within the bounded drain window and no force was requested, so the process
    ///     was <strong>left running</strong>. The eject did not complete; the caller should retry (optionally forcing).
    /// </summary>
    TimedOutStillBusy = 2,

    /// <summary>
    ///     In-flight inference did not finish within the bounded drain window and <c>force</c> was requested, so the
    ///     process was torn down anyway. The interrupted run is marked as operator-ejected (not a generic failure).
    /// </summary>
    ForcedWhileBusy = 3
}
