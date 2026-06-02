namespace XE_Local_AI_Engine.Client.Services.Monitoring;

/// <summary>
///     Cohort-monitoring verdict for a single Enabled playbook action (relevance retrieval and cohort monitoring, the cohort-monitor verdict rules). The signal is coarse and
///     agent-level (injection is not per-message attributable, the aggregate-feedback attribution rule), so it is advisory only: <see cref="Regressed" />
///     and <see cref="Flat" /> flag an action for human review, never an automatic disable.
/// </summary>
public enum PlaybookMonitorStatus
{
    /// <summary>Fewer than the minimum after-enable samples — no verdict is drawn and the action is never flagged.</summary>
    InsufficientData,

    /// <summary>The after-enable down-vote rate fell below the before-enable rate by more than the epsilon.</summary>
    Improved,

    /// <summary>The before/after down-vote rates are within the epsilon — no meaningful change.</summary>
    Flat,

    /// <summary>The after-enable down-vote rate rose above the before-enable rate by more than the epsilon.</summary>
    Regressed
}

/// <summary>
///     A read-only monitoring view of one Enabled playbook action: its enable timestamp, the before/after down-vote
///     rates over the agent's feedback cohort, the after-enable sample size, the derived <see cref="Status" />, whether it
///     is <see cref="Flagged" /> for human review, and the optional tool facet the action is scoped to.
/// </summary>
public sealed record PlaybookActionMonitorView(
    Guid ActionId,
    long EnabledAtUtc,
    double BeforeDownRate,
    double AfterDownRate,
    int AfterSampleSize,
    PlaybookMonitorStatus Status,
    bool Flagged,
    string? FacetToolName);

/// <summary>
///     Computes the cohort-monitoring view for every Enabled action of an agent. Invoked off the hot path (the monitor
///     GET endpoint / batch only), never during a send. The implementation is supplied in relevance retrieval and cohort monitoring background implementation (it depends
///     on the monitor store); this contract is pinned up front so the host endpoint and React signal can build against it.
/// </summary>
public interface IPlaybookMonitorService
{
    /// <summary>
    ///     Returns one <see cref="PlaybookActionMonitorView" /> per Enabled action that carries an enable timestamp, for
    ///     the agent identified by <paramref name="agentDefinitionId" />.
    /// </summary>
    Task<IReadOnlyList<PlaybookActionMonitorView>> GetMonitorAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default);
}
