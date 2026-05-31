namespace XE_Local_AI_Engine.Tests.Monitoring;

using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Monitoring;
using XE_Local_AI_Engine.Client.Services.Monitoring.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class PlaybookMonitorServiceTests
{
    private const double Tolerance = 1e-9;

    [Test]
    public async Task GetMonitorAsync_WhenAfterRateFallsBelowBefore_ReportsImprovedAndNotFlagged()
    {
        var agentId = Guid.NewGuid();
        // Before: 6/10 down (0.6). After: 1/10 down (0.1). Drop > epsilon (0.05) and after sample (10) >= floor (3).
        var service = CreateService(out _, agentId, new CohortComparison(BeforeTotal: 10, BeforeDown: 6, AfterTotal: 10, AfterDown: 1), enabledAtUtc: 100);

        var views = await service.GetMonitorAsync(agentId).ConfigureAwait(false);

        AssertEx.Equal(1, views.Count);
        var view = views[0];
        AssertEx.Equal(PlaybookMonitorStatus.Improved, view.Status);
        AssertEx.False(view.Flagged, "Improved is never flagged.");
        AssertEx.True(Math.Abs(view.BeforeDownRate - 0.6d) < Tolerance, "Before down-rate should be 0.6.");
        AssertEx.True(Math.Abs(view.AfterDownRate - 0.1d) < Tolerance, "After down-rate should be 0.1.");
        AssertEx.Equal(10, view.AfterSampleSize);
    }

    [Test]
    public async Task GetMonitorAsync_WhenAfterRateRisesAboveBefore_ReportsRegressedAndFlagged()
    {
        var agentId = Guid.NewGuid();
        // Before: 1/10 down (0.1). After: 8/10 down (0.8). Rise > epsilon and after sample >= floor → Regressed + flagged.
        var service = CreateService(out _, agentId, new CohortComparison(BeforeTotal: 10, BeforeDown: 1, AfterTotal: 10, AfterDown: 8), enabledAtUtc: 100);

        var views = await service.GetMonitorAsync(agentId).ConfigureAwait(false);

        AssertEx.Equal(1, views.Count);
        AssertEx.Equal(PlaybookMonitorStatus.Regressed, views[0].Status);
        AssertEx.True(views[0].Flagged, "A meaningfully-sampled regression must be flagged for review.");
    }

    [Test]
    public async Task GetMonitorAsync_WhenRatesWithinEpsilon_ReportsFlatAndFlagged()
    {
        var agentId = Guid.NewGuid();
        // Before: 5/10 (0.5). After: 5/10 (0.5). Within epsilon → Flat; after sample >= floor → flagged (dead action).
        var service = CreateService(out _, agentId, new CohortComparison(BeforeTotal: 10, BeforeDown: 5, AfterTotal: 10, AfterDown: 5), enabledAtUtc: 100);

        var views = await service.GetMonitorAsync(agentId).ConfigureAwait(false);

        AssertEx.Equal(PlaybookMonitorStatus.Flat, views[0].Status);
        AssertEx.True(views[0].Flagged, "A meaningfully-sampled flat action is flagged for review.");
    }

    [Test]
    public async Task GetMonitorAsync_WhenAfterSampleBelowFloor_ReportsInsufficientDataAndNeverFlagged()
    {
        var agentId = Guid.NewGuid();
        // After total 2 < the min sample size 3: regardless of the rate delta, the verdict is InsufficientData, unflagged.
        var service = CreateService(out _, agentId, new CohortComparison(BeforeTotal: 10, BeforeDown: 1, AfterTotal: 2, AfterDown: 2), enabledAtUtc: 100);

        var views = await service.GetMonitorAsync(agentId).ConfigureAwait(false);

        AssertEx.Equal(PlaybookMonitorStatus.InsufficientData, views[0].Status);
        AssertEx.False(views[0].Flagged, "InsufficientData is never flagged.");
        AssertEx.Equal(2, views[0].AfterSampleSize);
    }

    [Test]
    public async Task GetMonitorAsync_WhenActionHasScope_RequestsFacetAndSurfacesFacetToolName()
    {
        var agentId = Guid.NewGuid();
        var enabledAtUtc = 100L;
        var monitorStore = Substitute.For<IPlaybookMonitorStore>();
        var actionStore = Substitute.For<IPlaybookActionStore>();
        var scopedAction = EnabledAction(agentId, enabledAtUtc, scope: "run_in_agent_home");
        actionStore.ListEnabledByAgentAsync(agentId, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([scopedAction]));
        monitorStore.GetCohortComparisonAsync(agentId, enabledAtUtc, "run_in_agent_home", Arg.Any<CancellationToken>())
                    .Returns(new CohortComparison(BeforeTotal: 4, BeforeDown: 3, AfterTotal: 4, AfterDown: 0));
        var service = new PlaybookMonitorService(monitorStore, actionStore, DefaultOptions());

        var views = await service.GetMonitorAsync(agentId).ConfigureAwait(false);

        AssertEx.Equal(1, views.Count);
        AssertEx.Equal("run_in_agent_home", views[0].FacetToolName);
        AssertEx.Equal(PlaybookMonitorStatus.Improved, views[0].Status);
        // The facet (non-null tool scope) was requested from the store, not the overall (null) path.
        await monitorStore.Received(1).GetCohortComparisonAsync(agentId, enabledAtUtc, "run_in_agent_home", Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await monitorStore.DidNotReceive().GetCohortComparisonAsync(agentId, enabledAtUtc, null, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task GetMonitorAsync_WhenActionHasBlankScope_RequestsOverallWithNullScopeAndNullFacet()
    {
        var agentId = Guid.NewGuid();
        var enabledAtUtc = 100L;
        var monitorStore = Substitute.For<IPlaybookMonitorStore>();
        var actionStore = Substitute.For<IPlaybookActionStore>();
        // A whitespace scope must normalise to null so the store uses the overall (agent-level) cohort, not a facet keyed
        // on an empty tool name.
        var action = EnabledAction(agentId, enabledAtUtc, scope: "   ");
        actionStore.ListEnabledByAgentAsync(agentId, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([action]));
        monitorStore.GetCohortComparisonAsync(agentId, enabledAtUtc, null, Arg.Any<CancellationToken>())
                    .Returns(new CohortComparison(BeforeTotal: 10, BeforeDown: 5, AfterTotal: 10, AfterDown: 5));
        var service = new PlaybookMonitorService(monitorStore, actionStore, DefaultOptions());

        var views = await service.GetMonitorAsync(agentId).ConfigureAwait(false);

        AssertEx.True(views[0].FacetToolName is null, "A blank scope must surface a null facet.");
        await monitorStore.Received(1).GetCohortComparisonAsync(agentId, enabledAtUtc, null, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task GetMonitorAsync_WhenActionHasNoEnabledAtClock_SkipsItWithoutQueryingStore()
    {
        var agentId = Guid.NewGuid();
        var monitorStore = Substitute.For<IPlaybookMonitorStore>();
        var actionStore = Substitute.For<IPlaybookActionStore>();
        var noClock = EnabledAction(agentId, enabledAtUtc: null, scope: null);
        actionStore.ListEnabledByAgentAsync(agentId, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([noClock]));
        var service = new PlaybookMonitorService(monitorStore, actionStore, DefaultOptions());

        var views = await service.GetMonitorAsync(agentId).ConfigureAwait(false);

        AssertEx.Equal(0, views.Count);
        await monitorStore.DidNotReceive().GetCohortComparisonAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task GetMonitorAsync_WhenNoEnabledActions_ReturnsEmptyList()
    {
        var agentId = Guid.NewGuid();
        var monitorStore = Substitute.For<IPlaybookMonitorStore>();
        var actionStore = Substitute.For<IPlaybookActionStore>();
        actionStore.ListEnabledByAgentAsync(agentId, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        var service = new PlaybookMonitorService(monitorStore, actionStore, DefaultOptions());

        var views = await service.GetMonitorAsync(agentId).ConfigureAwait(false);

        AssertEx.True(views is not null, "The result is non-null.");
        AssertEx.Equal(0, views!.Count);
    }

    private static PlaybookMonitorService CreateService(out IPlaybookMonitorStore monitorStore,
        Guid agentId,
        CohortComparison comparison,
        long enabledAtUtc)
    {
        monitorStore = Substitute.For<IPlaybookMonitorStore>();
        var actionStore = Substitute.For<IPlaybookActionStore>();
        actionStore.ListEnabledByAgentAsync(agentId, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([EnabledAction(agentId, enabledAtUtc, scope: null)]));
        monitorStore.GetCohortComparisonAsync(agentId, enabledAtUtc, null, Arg.Any<CancellationToken>()).Returns(comparison);
        return new PlaybookMonitorService(monitorStore, actionStore, DefaultOptions());
    }

    private static IOptions<PlaybookMonitorOptions> DefaultOptions()
    {
        return Options.Create(new PlaybookMonitorOptions { ImprovementEpsilon = 0.05d, MinSampleSize = 3 });
    }

    private static PlaybookActionRecord EnabledAction(Guid agentDefinitionId, long? enabledAtUtc, string? scope)
    {
        return new PlaybookActionRecord(Guid.NewGuid(),
            agentDefinitionId,
            PlaybookActionState.Enabled,
            PlaybookActionSource.Manual,
            TriggerCondition: null,
            "Prefer small commits.",
            scope,
            Priority: 10,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10,
            EnabledAtUtc: enabledAtUtc);
    }
}
