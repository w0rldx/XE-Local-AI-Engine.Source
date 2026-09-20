namespace XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     What one reconciliation pass found and did. Every number is a count rather than a list: the pass writes what
///     it decided to the instance rows and their event feed.
/// </summary>
/// <remarks>
///     <see cref="ForeignInstallContainers" /> is the one number not about this installation's own rows: it counts
///     containers wearing this feature's owner label under a DIFFERENT install id — a data directory that moved, most
///     often — which are deliberately never removed and would otherwise be invisible while holding loopback ports.
///     <see cref="RowsFailed" /> counts rows whose own judgement threw; those keep the status they had and the pass
///     carries on, because one unjudgeable instance must not cost every later row its verdict.
/// </remarks>
public sealed record ExternalAppReconcileSummary
{
    public required int RowsInspected { get; init; }

    public required int RowsChanged { get; init; }

    public required int OrphansRemoved { get; init; }

    public required int ForeignInstallContainers { get; init; }

    public required int RowsSkippedBusy { get; init; }

    public int RowsFailed { get; init; }

    /// <summary>The pass that judged nothing: the feature is off, or no container runtime was ready.</summary>
    public static ExternalAppReconcileSummary Nothing { get; } = new()
    {
        RowsInspected = 0,
        RowsChanged = 0,
        OrphansRemoved = 0,
        ForeignInstallContainers = 0,
        RowsSkippedBusy = 0
    };
}

/// <summary>
///     Settles what a previous run of this engine left behind: rows stuck in a transient status because the host died
///     mid-operation, rows whose containers kept running or stopped without us, and containers no row claims.
/// </summary>
/// <remarks>
///     A public entry point rather than a private startup body, because it runs twice: from <c>StartAsync</c>, and
///     again from the operator-facing runtime refresh after a successful preflight. A daemon that was down at boot
///     would otherwise leave every row unsettled until the next engine restart.
/// </remarks>
public interface IExternalAppStartupReconciler
{
    /// <summary>Judges every stored instance against the daemon exactly once.</summary>
    /// <remarks>
    ///     Never throws for an unreachable daemon and never writes a row when the runtime is not ready: "there is no
    ///     container runtime right now" is a response field, not a state to flatten durable rows into.
    /// </remarks>
    Task<ExternalAppReconcileSummary> ReconcileAsync(CancellationToken cancellationToken = default);
}
