namespace XE_Local_AI_Engine.Client.Services.Monitoring.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Default <see cref="IPlaybookMonitorService" /> (Playbook P5, plan §3.2). For every Enabled action of the agent
///     that carries an <c>EnabledAtUtc</c> clock, it reads the agent's before/after feedback cohort from
///     <see cref="IPlaybookMonitorStore" /> (faceted by the action's tool scope when set), derives the before/after
///     down-vote rates, and classifies the change against the configured epsilon and minimum sample size. The verdict is
///     advisory: Flat/Regressed flag the action for human review (never an auto-disable), and a cohort below the minimum
///     sample size is InsufficientData and never flagged. Computed on read, off the hot path, model-free and deterministic.
/// </summary>
public sealed class PlaybookMonitorService(
    IPlaybookMonitorStore monitorStore,
    IPlaybookActionStore playbookActionStore,
    IOptions<PlaybookMonitorOptions> monitorOptions) : IPlaybookMonitorService
{
    private readonly IPlaybookMonitorStore _monitorStore = monitorStore ?? throw new ArgumentNullException(nameof(monitorStore));
    private readonly IPlaybookActionStore _playbookActionStore = playbookActionStore ?? throw new ArgumentNullException(nameof(playbookActionStore));
    private readonly PlaybookMonitorOptions _options = (monitorOptions ?? throw new ArgumentNullException(nameof(monitorOptions))).Value;

    public async Task<IReadOnlyList<PlaybookActionMonitorView>> GetMonitorAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default)
    {
        var enabled = await _playbookActionStore.ListEnabledByAgentAsync(agentDefinitionId, cancellationToken).ConfigureAwait(false);

        var views = new List<PlaybookActionMonitorView>(enabled.Count);
        foreach (var action in enabled)
        {
            // An action with no enable clock has no before/after split to measure, for instance a row whose stamp
            // predates Playbook P5, so skip it rather than fabricate a cohort.
            if (action.EnabledAtUtc is not { } enabledAtUtc)
            {
                continue;
            }

            // A blank scope is the overall (agent-level) cohort; a non-blank scope requests the per-tool facet. The
            // store treats any non-null toolScope as a facet, so blank must be normalised to null here.
            // Note: the per-tool facet only lights up when Scope equals an actual tool_events.tool_name. A free-text
            // scope yields an empty facet cohort, which classifies as InsufficientData and is never flagged — benign by design.
            var facetToolName = string.IsNullOrWhiteSpace(action.Scope) ? null : action.Scope;

            var comparison = await _monitorStore.GetCohortComparisonAsync(agentDefinitionId, enabledAtUtc, facetToolName, cancellationToken).ConfigureAwait(false);
            views.Add(BuildView(action.Id, enabledAtUtc, comparison, facetToolName));
        }

        return views;
    }

    private PlaybookActionMonitorView BuildView(Guid actionId, long enabledAtUtc, CohortComparison comparison, string? facetToolName)
    {
        var beforeDownRate = DownRate(comparison.BeforeDown, comparison.BeforeTotal);
        var afterDownRate = DownRate(comparison.AfterDown, comparison.AfterTotal);
        var afterSampleSize = comparison.AfterTotal;

        var status = ClassifyStatus(beforeDownRate, afterDownRate, afterSampleSize);

        // InsufficientData is never flagged; Improved is a good outcome. Only a meaningfully-sampled Flat/Regressed
        // verdict raises the human-review flag.
        var flagged = afterSampleSize >= _options.MinSampleSize && status is PlaybookMonitorStatus.Flat or PlaybookMonitorStatus.Regressed;

        return new PlaybookActionMonitorView(actionId,
            enabledAtUtc,
            beforeDownRate,
            afterDownRate,
            afterSampleSize,
            status,
            flagged,
            facetToolName);
    }

    private PlaybookMonitorStatus ClassifyStatus(double beforeDownRate, double afterDownRate, int afterSampleSize)
    {
        if (afterSampleSize < _options.MinSampleSize)
        {
            return PlaybookMonitorStatus.InsufficientData;
        }

        if (afterDownRate < beforeDownRate - _options.ImprovementEpsilon)
        {
            return PlaybookMonitorStatus.Improved;
        }

        if (afterDownRate > beforeDownRate + _options.ImprovementEpsilon)
        {
            return PlaybookMonitorStatus.Regressed;
        }

        return PlaybookMonitorStatus.Flat;
    }

    private static double DownRate(int down, int total)
    {
        return total == 0 ? 0d : (double)down / total;
    }
}
