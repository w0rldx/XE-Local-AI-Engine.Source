namespace XE_Local_AI_Engine.Client.Services.Monitoring;

/// <summary>
///     Cohort-monitoring verdict for a single Enabled playbook action (relevance retrieval and cohort monitoring, the cohort-monitor
///     verdict rules).
/// </summary>
/// <remarks>
///     The signal is coarse and agent-level (injection is not per-message attributable, the aggregate-feedback attribution rule), so it is
///     advisory only: <see cref="Regressed" /> and <see cref="Flat" /> flag an action for human review, never an automatic disable.
/// </remarks>
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

/// <summary>A read-only monitoring view of one Enabled playbook action.</summary>
/// <remarks>
///     Carries its enable timestamp, the before/after down-vote rates over the agent's feedback cohort, the after-enable
///     sample size, the derived <see cref="Status" />, whether it is <see cref="Flagged" /> for human review, and the
///     optional tool facet the action is scoped to.
/// </remarks>
public sealed class PlaybookActionMonitorView
{
    public required Guid ActionId { get; init; }

    public required long EnabledAtUtc { get; init; }

    public required double BeforeDownRate { get; init; }

    public required double AfterDownRate { get; init; }

    public required int AfterSampleSize { get; init; }

    public required PlaybookMonitorStatus Status { get; init; }

    public required bool Flagged { get; init; }

    public required string? FacetToolName { get; init; }
}
