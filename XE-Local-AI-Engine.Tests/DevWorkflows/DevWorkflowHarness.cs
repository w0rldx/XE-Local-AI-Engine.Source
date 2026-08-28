namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Drives the real dispatcher over the real store, one tick at a time.
///     <para>
///         Nothing here waits on a timer or races a background task: the sweep interval is set past any test's lifetime,
///         and every advance is an explicit <c>AdvanceOnceAsync</c> call. That is why <c>AdvanceOnceAsync</c> is a design
///         requirement rather than a convenience — a runtime whose only entry point were a hosted loop could only be
///         tested by sleeping and hoping.
///     </para>
///     <para>
///         A restart is simulated the way the reconciler will be: the same database, a fresh dispatcher. No host restart
///         is needed, because the dispatcher holds nothing but a graph cache.
///     </para>
/// </summary>
internal sealed class DevWorkflowHarness : IAsyncDisposable
{
    /// <summary>
    ///     The sweep interval is set past any test's lifetime. The hosted loop still starts — its wiring is worth
    ///     exercising — but every advance a test asserts on is one the test asked for.
    /// </summary>
    private readonly TestServerWebAppFactory _factory = new()
    {
        AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DevWorkflows:Enabled"] = "true",
            ["WorkSessions:Enabled"] = "true",
            ["DevWorkflows:SweepSeconds"] = "3600"
        }
    };

    public IServiceProvider Services => _factory.Services;

    /// <summary>The dispatcher under test. Resolved from the real container, so its wiring is under test too.</summary>
    public DevWorkflowDispatcher Dispatcher => Services.GetRequiredService<DevWorkflowDispatcher>();

    public DevWorkflowGraphCache Graphs => Services.GetRequiredService<DevWorkflowGraphCache>();

    /// <summary>
    ///     A second dispatcher over the same database, standing in for the one a restart would build. It gets a fresh
    ///     graph cache because that is exactly what a restart loses — and losing it must cost nothing but a re-parse.
    /// </summary>
    public DevWorkflowDispatcher CreateReplacementDispatcher() =>
        new(Services.GetRequiredService<IServiceScopeFactory>(),
            new DevWorkflowGraphCache(),
            Services.GetRequiredService<IOptions<DevWorkflowOptions>>(),
            Services.GetRequiredService<TimeProvider>(),
            Services.GetRequiredService<ILogger<DevWorkflowDispatcher>>());

    public ValueTask DisposeAsync() =>
        _factory.DisposeAsync();

    /// <summary>Creates a work item, a definition on <paramref name="graphJson" />, and a run pinned to it.</summary>
    public async Task<Guid> StartRunAsync(string graphJson, string request = "Explain how the inference path works.")
    {
        await using var scope = Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
        var workItem = await store.CreateWorkItemAsync(new CreateDevWorkflowWorkItemCommand(Guid.NewGuid(), "Seeded work item", request)).ConfigureAwait(false);
        var definition = await store.CreateDefinitionAsync(new CreateDevWorkflowDefinitionCommand(Guid.NewGuid(), "Seeded definition", graphJson, NodeCount: 1))
                                    .ConfigureAwait(false);
        var run = await store.StartRunAsync(new StartDevWorkflowRunCommand(Guid.NewGuid(),
                                 workItem.Id,
                                 definition.Id,
                                 definition.Version,
                                 definition.GraphHash,
                                 graphJson))
                             .ConfigureAwait(false);
        return run.Id;
    }

    public Task<int> AdvanceAsync(Guid runId) =>
        Dispatcher.AdvanceOnceAsync(runId, CancellationToken.None);

    /// <summary>
    ///     Ticks until the run stops changing, and answers how many ticks it took. Bounded rather than open-ended: a run
    ///     that will not settle is a bug this must report as one, not hang on.
    /// </summary>
    public async Task<int> AdvanceUntilQuiescentAsync(Guid runId, int maxTicks = 20)
    {
        for (var tick = 1; tick <= maxTicks; tick++)
        {
            if (await AdvanceAsync(runId).ConfigureAwait(false) == 0)
            {
                return tick;
            }
        }

        throw new AssertionException($"Run {runId} was still writing transitions after {maxTicks} ticks.");
    }

    public async Task<DevWorkflowRunSnapshot> ReadRunAsync(Guid runId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().GetRunAsync(runId).ConfigureAwait(false);
    }

    public async Task<DevWorkflowWorkItemSnapshot> ReadWorkItemAsync(Guid runId)
    {
        await using var scope = Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
        var run = await store.GetRunAsync(runId).ConfigureAwait(false);
        return await store.GetWorkItemAsync(run.WorkItemId).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DevWorkflowNodeRunSnapshot>> ReadNodeRunsAsync(Guid runId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().ListNodeRunsAsync(runId).ConfigureAwait(false);
    }

    public async Task<DevWorkflowNodeRunSnapshot> ReadNodeRunAsync(Guid runId, string nodeKey)
    {
        var nodeRuns = await ReadNodeRunsAsync(runId).ConfigureAwait(false);
        return nodeRuns.SingleOrDefault(nodeRun => string.Equals(nodeRun.NodeKey, nodeKey, StringComparison.Ordinal))
               ?? throw new AssertionException($"Run {runId} carries no node run for '{nodeKey}'.");
    }

    public async Task<IReadOnlyList<DevWorkflowRunEventSnapshot>> ReadEventsAsync(Guid runId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().ListEventsAsync(runId, sinceSequence: 0, limit: 500).ConfigureAwait(false);
    }

    /// <summary>The event types in order, which is what most assertions here are actually about.</summary>
    public async Task<string> ReadEventTrailAsync(Guid runId) =>
        string.Join(", ", (await ReadEventsAsync(runId).ConfigureAwait(false)).Select(static entry => entry.EventType));

    /// <summary>Records a decision the way the endpoint will: a durable row the next tick turns into a transition.</summary>
    public async Task DecideAsync(Guid runId, string nodeKey, DevWorkflowDecisionKind decision, string? subject = "operator")
    {
        var nodeRun = await ReadNodeRunAsync(runId, nodeKey).ConfigureAwait(false);
        await using var scope = Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>()
                       .RecordDecisionAsync(new RecordDevWorkflowDecisionCommand(runId,
                           Guid.NewGuid(),
                           nodeRun.Id,
                           DevWorkflowVersions.Any,
                           Guid.NewGuid(),
                           decision,
                           DecidedBySubject: subject))
                       .ConfigureAwait(false);
    }

    /// <summary>Moves the run itself, the way the run service's fire-and-forget commands will.</summary>
    public async Task TransitionRunAsync(Guid runId, DevWorkflowRunStatus target)
    {
        await using var scope = Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>()
                       .TransitionRunAsync(new TransitionDevWorkflowRunCommand(runId, DevWorkflowVersions.Any, target))
                       .ConfigureAwait(false);
    }
}
