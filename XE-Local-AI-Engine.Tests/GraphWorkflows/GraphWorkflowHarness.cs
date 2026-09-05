namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Drives the real dispatcher over the real store, one tick at a time.
///     <para>
///         Nothing here waits on a timer or races a background task: the test host strips every hosted service, so the
///         signal and sweep pumps never run, and every advance is an explicit <c>AdvanceOnceAsync</c> call. That is why
///         that method is a design requirement rather than a convenience — a runtime whose only entry point were a
///         hosted loop could be tested only by sleeping and hoping.
///     </para>
///     <para>
///         A restart is simulated the way the reconciler will be: the same database, a fresh dispatcher. No host
///         restart is needed, because the dispatcher holds nothing but a parsed-graph cache.
///     </para>
/// </summary>
internal sealed class GraphWorkflowHarness : IAsyncDisposable
{
    private readonly TestServerWebAppFactory _factory;

    /// <summary>Whether this harness built the host and so has to tear it down; false for the class's shared one.</summary>
    private readonly bool _ownsFactory;

    private GraphWorkflowDispatcher? _replacement;

    /// <summary>
    ///     A host of this test's own. Take one only to hold host-level state a concurrent sibling must not see — a
    ///     config value, or an absolute row count — and say which at the construction site.
    /// </summary>
    public GraphWorkflowHarness(params (string Key, string Value)[] configuration)
    {
        _factory = GraphWorkflowHostFixture.NewFactory(configuration);
        _ownsFactory = true;
    }

    /// <summary>The class's shared host, with this test's own runs on it. See <see cref="GraphWorkflowHostFixture" />.</summary>
    public GraphWorkflowHarness(GraphWorkflowHostFixture host) =>
        _factory = (host ?? throw new ArgumentNullException(nameof(host))).Factory;

    public IServiceProvider Services => _factory.Services;

    /// <summary>The container's dispatcher, so its wiring is under test too — or the replacement a restart installed.</summary>
    public GraphWorkflowDispatcher Dispatcher => _replacement ?? Services.GetRequiredService<GraphWorkflowDispatcher>();

    /// <summary>The recorded signal every command path calls. A container singleton: count only your own run id.</summary>
    public RecordingGraphWorkflowDispatcherSignal Signals =>
        (RecordingGraphWorkflowDispatcherSignal)Services.GetRequiredService<IGraphWorkflowDispatcherSignal>();

    /// <summary>
    ///     A second dispatcher over the same database, standing in for the one a restart would build. It gets a fresh
    ///     graph cache because that is exactly what a restart loses — and losing it must cost nothing but a re-parse.
    ///     <para>
    ///         <paramref name="clock" /> is how a deadline is reached without waiting for one: the dispatcher reads its
    ///         time provider only to decide whether a running row is past its node's timeout.
    ///     </para>
    /// </summary>
    public GraphWorkflowDispatcher CreateReplacementDispatcher(bool enabled = true, TimeProvider? clock = null)
    {
        _replacement = new GraphWorkflowDispatcher(Services.GetRequiredService<IServiceScopeFactory>(),
            Services.GetRequiredService<GraphWorkflowInlineExecutor>(),

            // The container's lanes, not a second set: slot counts are a property of the NODE, and a restart that
            // handed itself fresh ones would be simulating a machine with twice the capacity.
            Services.GetServices<IGraphWorkflowNodeExecutor>(),
            Options.Create(new GraphWorkflowOptions
            {
                Enabled = enabled,
                MaxConcurrentRuns = CurrentOptions().MaxConcurrentRuns,
                MaxTotalAttempts = CurrentOptions().MaxTotalAttempts,
                MaxOutputJsonBytes = CurrentOptions().MaxOutputJsonBytes,
                DefaultNodeTimeoutSeconds = CurrentOptions().DefaultNodeTimeoutSeconds,
                DispatchIntervalMilliseconds = CurrentOptions().DispatchIntervalMilliseconds
            }),
            clock ?? Services.GetRequiredService<TimeProvider>(),
            Services.GetRequiredService<ILogger<GraphWorkflowDispatcher>>());
        return _replacement;
    }

    public async ValueTask DisposeAsync()
    {
        if (_replacement is { } replacement)
        {
            await replacement.DisposeAsync().ConfigureAwait(false);
        }

        if (_ownsFactory)
        {
            await _factory.DisposeAsync().ConfigureAwait(false);
        }
    }

    public GraphWorkflowOptions CurrentOptions() =>
        Services.GetRequiredService<IOptions<GraphWorkflowOptions>>().Value;

    /// <summary>
    ///     Whether the dispatcher was told to look at this run by its OWN loop. Draining rather than peeking, because
    ///     the assertion is "that tick re-signalled" and a later one must not read this one's signal.
    /// </summary>
    public bool WasSignalled(Guid runId)
    {
        var found = false;
        while (Dispatcher.PendingSignals.TryRead(out var signalled))
        {
            found |= signalled == runId;
        }

        return found;
    }

    public Task<int> AdvanceAsync(Guid runId) =>
        Dispatcher.AdvanceOnceAsync(runId, CancellationToken.None);

    /// <summary>One tick through the production wrapper, which is the only thing that re-signals a productive tick.</summary>
    public Task AdvanceSafelyAsync(Guid runId) =>
        Dispatcher.AdvanceSafelyAsync(runId, CancellationToken.None);

    /// <summary>
    ///     Ticks until the run stops changing, and answers how many ticks it took. Bounded rather than open-ended: a
    ///     run that will not settle is a bug this must report as one, not hang on.
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

    public async Task<Guid> SeedDefinitionAsync(string graphJson)
    {
        await using var scope = Services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>();
        return (await definitions.CreateAsync($"Seeded {Guid.NewGuid():N}", description: null, graphJson).ConfigureAwait(false)).Id;
    }

    /// <summary>A definition and a run of it, through the real command surface.</summary>
    public async Task<Guid> StartRunAsync(string graphJson, string? inputJson = null)
    {
        var definitionId = await SeedDefinitionAsync(graphJson).ConfigureAwait(false);
        return await StartRunOfAsync(definitionId, inputJson).ConfigureAwait(false);
    }

    public async Task<Guid> StartRunOfAsync(Guid definitionId, string? inputJson = null)
    {
        await using var scope = Services.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<IGraphWorkflowRunService>();
        return (await runs.StartAsync(definitionId, Guid.NewGuid(), inputJson, definitionVersion: null).ConfigureAwait(false)).Run.Id;
    }

    /// <summary>
    ///     A run started through the STORE, so a test can pin a graph, or a set of node runs, the run service would
    ///     never produce — a graph that no longer parses, or a node key the graph does not declare.
    /// </summary>
    public async Task<Guid> StartRunThroughTheStoreAsync(Guid definitionId, string pinnedGraphJson, IReadOnlyList<(string NodeKey, GraphWorkflowNodeKind Kind)> nodeRuns)
    {
        ArgumentNullException.ThrowIfNull(nodeRuns);

        await using var scope = Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>();
        var definition = await store.GetDefinitionAsync(definitionId).ConfigureAwait(false);
        var run = await store.StartRunAsync(new StartGraphWorkflowRunCommand(Guid.NewGuid(),
                                 Guid.NewGuid(),
                                 definitionId,
                                 definition.Version,
                                 definition.GraphHash,
                                 pinnedGraphJson,
                                 InputJson: null,
                                 [.. nodeRuns.Select(seed => new GraphWorkflowNodeRunSeed(Guid.NewGuid(), seed.NodeKey, seed.Kind))]))
                             .ConfigureAwait(false);
        return run.Id;
    }

    public async Task CancelAsync(Guid runId)
    {
        await using var scope = Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<IGraphWorkflowRunService>().CancelAsync(runId).ConfigureAwait(false);
    }

    public async Task<GraphWorkflowRunSnapshot> ReadRunAsync(Guid runId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().GetRunAsync(runId).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GraphWorkflowNodeRunSnapshot>> ReadNodeRunsAsync(Guid runId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().ListNodeRunsAsync(runId).ConfigureAwait(false);
    }

    public async Task<GraphWorkflowNodeRunSnapshot> ReadNodeRunAsync(Guid runId, string nodeKey)
    {
        var nodeRuns = await ReadNodeRunsAsync(runId).ConfigureAwait(false);
        return nodeRuns.SingleOrDefault(nodeRun => string.Equals(nodeRun.NodeKey, nodeKey, StringComparison.Ordinal))
               ?? throw new AssertionException($"Run {runId} carries no node run for '{nodeKey}'.");
    }

    public async Task<IReadOnlyList<GraphWorkflowRunEventSnapshot>> ReadEventsAsync(Guid runId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().ListEventsAsync(runId, afterSeq: 0, limit: 500).ConfigureAwait(false);
    }

    /// <summary>The event types in order, which is what most assertions here are actually about.</summary>
    public async Task<string> ReadEventTrailAsync(Guid runId) =>
        string.Join(", ", (await ReadEventsAsync(runId).ConfigureAwait(false)).Select(static entry => entry.EventType));

    /// <summary>
    ///     Moves one node run through the STORE, which is how a test stands a row in a state only an executor this
    ///     build does not have could otherwise produce — a failure class, or an attempt past the first.
    /// </summary>
    public async Task TransitionNodeRunAsync(Guid runId,
        string nodeKey,
        GraphWorkflowNodeRunStatus target,
        GraphWorkflowFailureClass? failureClass = null,
        string? terminalReason = null,
        bool incrementAttempt = false)
    {
        var nodeRun = await ReadNodeRunAsync(runId, nodeKey).ConfigureAwait(false);
        await using var scope = Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>()
                       .TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(runId,
                           nodeRun.Id,
                           GraphWorkflowVersions.Any,
                           target,
                           FailureClass: failureClass,
                           TerminalReason: terminalReason,
                           IncrementAttempt: incrementAttempt))
                       .ConfigureAwait(false);
    }
}

/// <summary>A clock stopped at one instant — enough to put a running node run past its deadline without waiting for one.</summary>
internal sealed class GraphWorkflowFixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() =>
        now;
}
