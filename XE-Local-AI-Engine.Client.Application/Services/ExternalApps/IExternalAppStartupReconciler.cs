namespace XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     What one reconciliation pass found and did. Every number is a count rather than a list: the pass writes what
///     it decided to the instance rows and their event feed, and a caller that needs the detail reads those.
///     <para>
///         <see cref="ForeignInstallContainers" /> is the one number that is not about this installation's own rows.
///         It counts containers wearing this feature's owner label under a DIFFERENT install id — a data directory
///         that moved, most often — which are deliberately never removed and would otherwise be invisible while
///         holding loopback ports.
///     </para>
///     <para>
///         <see cref="RowsFailed" /> counts rows whose own judgement threw — a container removed between the list
///         and the inspect, a stored snapshot the policy now refuses. Those rows keep the status they had and the
///         pass carries on, because one unjudgeable instance must not cost every later row its verdict.
///     </para>
/// </summary>
public sealed record ExternalAppReconcileSummary(int RowsInspected,
    int RowsChanged,
    int OrphansRemoved,
    int ForeignInstallContainers,
    int RowsSkippedBusy,
    int RowsFailed = 0)
{
    /// <summary>The pass that judged nothing: the feature is off, or no container runtime was ready.</summary>
    public static ExternalAppReconcileSummary Nothing { get; } = new(RowsInspected: 0,
        RowsChanged: 0,
        OrphansRemoved: 0,
        ForeignInstallContainers: 0,
        RowsSkippedBusy: 0);
}

/// <summary>
///     Settles what a previous run of this engine left behind: rows stuck in a transient status because the host died
///     mid-operation, rows whose containers kept running or stopped without us, and containers no row claims.
///     <para>
///         A public entry point rather than a private startup body, because it is run twice: once from
///         <c>StartAsync</c>, and again by the operator-facing runtime refresh after a successful preflight. A daemon
///         that was down at boot would otherwise leave every row unsettled until the next engine restart.
///     </para>
/// </summary>
public interface IExternalAppStartupReconciler
{
    /// <summary>
    ///     Judges every stored instance against the daemon exactly once. Never throws for an unreachable daemon and
    ///     never writes a row when the runtime is not ready: "there is no container runtime right now" is a response
    ///     field, not a state to flatten durable rows into.
    /// </summary>
    Task<ExternalAppReconcileSummary> ReconcileAsync(CancellationToken cancellationToken = default);
}
