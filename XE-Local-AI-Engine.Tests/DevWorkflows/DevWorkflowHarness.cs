namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Core.Interfaces;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     One workflow host for a whole test class (<c>ClassDataSource(SharedType.PerClass)</c>), so a class pays the
///     ~1.2 s host build once instead of once per test. Each test still gets its own <see cref="DevWorkflowHarness" />
///     over it — the harness itself is stateless beyond the replacement dispatcher a restart installs.
///     <para>
///         The host is shared, so three things on it are shared too, and a test that touches any of them keeps a
///         private host and says so at the construction site:
///         <list type="bullet">
///             <item>the SQLite database — scope every read to your own run id, and assert on no absolute row count;</item>
///             <item>
///                 the <see cref="FakeDevWorkflowAgentSession" />, which is a container singleton: its
///                 <c>HasCapacity</c>, <c>RefuseStart</c>, <c>RefuseCreateWith</c> and <c>OnDeleting</c> switches are
///                 host-wide, and its <c>Created</c>, <c>Objectives</c> and <c>Calls</c> lists accumulate every
///                 sibling's traffic (a <c>Calls</c> assertion filtered to your own session id is still safe);
///             </item>
///             <item>
///                 the dispatcher singleton, whose signal channel <see cref="DevWorkflowHarness.WasSignalled" /> DRAINS
///                 — one test's drain would eat a concurrent sibling's signal.
///             </item>
///         </list>
///     </para>
///     <para>
///         <c>MaxConcurrentRuns</c> is raised to the schema maximum because the cap counts <c>Running</c> runs across
///         the whole DATABASE: at the product default of four, the fifth concurrent test in a class would sit Pending
///         forever. The cap's own behaviour is asserted by two tests in <c>DevWorkflowDispatcherTests</c>, which pin it
///         explicitly on private hosts.
///     </para>
/// </summary>
public sealed class DevWorkflowHostFixture : IAsyncInitializer, IAsyncDisposable
{
    public TestServerWebAppFactory Factory { get; } = DevWorkflowHarness.NewFactory(("DevWorkflows:MaxConcurrentRuns", "64"));

    public Task InitializeAsync() =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() =>
        Factory.DisposeAsync();
}

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
    ///     The test host removes every hosted service, so the dispatcher's signal and sweep pumps never run here and
    ///     every advance is one a test asked for. The sweep interval is set past any test's lifetime anyway, so this
    ///     stays deterministic if that ever changes — but the pumps themselves are covered by unit-level assertions on
    ///     <c>AdvanceSafelyAsync</c> rather than by this harness.
    /// </summary>
    private readonly TestServerWebAppFactory _factory;

    /// <summary>Whether this harness built the host and so has to tear it down; false for the class's shared one.</summary>
    private readonly bool _ownsFactory;

    private DevWorkflowDispatcher? _replacement;

    /// <summary>
    ///     A host of this test's own. Take one only to hold host-level state a concurrent sibling must not see — a
    ///     config value, one of the fake agent's switches, an absolute row count, or the signal channel — and say which
    ///     at the construction site. Everything else shares the class host (see <see cref="DevWorkflowHostFixture" />).
    /// </summary>
    public DevWorkflowHarness(params (string Key, string Value)[] configuration)
    {
        _factory = NewFactory(configuration);
        _ownsFactory = true;
    }

    /// <summary>
    ///     A host of this test's own with one more service replaced — for standing a dependency in a state only it can
    ///     produce, such as a blob store that refuses one write.
    /// </summary>
    public DevWorkflowHarness(Action<IServiceCollection> configureServices, params (string Key, string Value)[] configuration)
    {
        _factory = NewFactory(drifting: false, fakeTools: true, configuration, configureServices);
        _ownsFactory = true;
    }

    private DevWorkflowHarness(bool drifting, bool fakeTools, (string Key, string Value)[] configuration)
    {
        _factory = NewFactory(drifting, fakeTools, configuration);
        _ownsFactory = true;
    }

    /// <summary>
    ///     A host of this test's own whose session reads move a node run underneath the reader — a second writer racing
    ///     startup recovery, made deterministic. Name the row to move through <see cref="Drift" />.
    /// </summary>
    public static DevWorkflowHarness WithASecondWriter() =>
        new(drifting: true, fakeTools: true, []);

    /// <summary>
    ///     A host of this test's own whose tool nodes really do prepare a workspace and run their commands. Slow by
    ///     construction — it clones a repository and runs a build — so it is for the handful of tests whose whole point
    ///     is that the substrate works, and it needs Git and the .NET SDK on the machine running it.
    /// </summary>
    public static DevWorkflowHarness WithARealSandbox(params (string Key, string Value)[] configuration) =>
        new(drifting: false, fakeTools: false, configuration);

    /// <summary>
    ///     A host of this test's own with the development chain scripted, and a clock of its own when one is given. The
    ///     chain is a container singleton whose history every test using it reads, so it is always a private host.
    /// </summary>
    public static DevWorkflowHarness WithAScriptedChain(TimeProvider? clock = null, Action<IServiceCollection>? configureServices = null) =>
        new(services =>
        {
            services.RemoveAll<IDevelopmentManagementService>();
            services.AddSingleton<IDevelopmentManagementService>(provider => new FakeDevelopmentTaskChain(provider.GetRequiredService<IServiceScopeFactory>()));
            if (clock is not null)
            {
                services.AddSingleton(clock);
            }

            configureServices?.Invoke(services);
        });

    /// <summary>The class's shared host, with this test's own runs on it. See <see cref="DevWorkflowHostFixture" />.</summary>
    public DevWorkflowHarness(DevWorkflowHostFixture host) =>
        _factory = (host ?? throw new ArgumentNullException(nameof(host))).Factory;

    /// <summary>The workflow host shape: the feature on, no sweep inside any test's lifetime, and the agent seam faked.</summary>
    internal static TestServerWebAppFactory NewFactory(params (string Key, string Value)[] configuration) =>
        NewFactory(drifting: false, fakeTools: true, configuration);

    private static TestServerWebAppFactory NewFactory(bool drifting,
        bool fakeTools,
        (string Key, string Value)[] configuration,
        Action<IServiceCollection>? configureServices = null)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DevWorkflows:Enabled"] = "true",
            ["WorkSessions:Enabled"] = "true",
            ["DevWorkflows:SweepSeconds"] = "3600"
        };
        foreach (var (key, value) in configuration)
        {
            settings[key] = value;
        }

        return new TestServerWebAppFactory
        {
            AdditionalConfiguration = settings,

            // The agent lane's one seam, replaced wholesale: everything else — the store, the blob stores, the graph,
            // the dispatcher — is the real thing, and only the part that would need a model is scripted.
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IWorkflowOwnedWorkSessionLifecycle>();
                services.AddSingleton<IWorkflowOwnedWorkSessionLifecycle>(provider =>
                {
                    var scopes = provider.GetRequiredService<IServiceScopeFactory>();
                    var agent = new FakeDevWorkflowAgentSession(scopes);
                    return drifting ? new DriftingWorkSessions(agent, scopes) : agent;
                });

                if (fakeTools)
                {
                    // The sandbox lane's one seam, replaced wholesale for the same reason the agent's is: everything
                    // above it — the slots, the in-flight registry, the report artifact, the rows — stays real.
                    services.RemoveAll<IDevWorkflowToolCommands>();
                    services.AddSingleton<IDevWorkflowToolCommands, FakeDevWorkflowToolCommands>();
                }

                configureServices?.Invoke(services);
            }
        };
    }

    public IServiceProvider Services => _factory.Services;

    /// <summary>
    ///     The scripted agent. Resolving the seam is what builds it, because a test scripts the agent BEFORE the first
    ///     tick asks the container for one.
    /// </summary>
    public FakeDevWorkflowAgentSession Agent =>
        Services.GetRequiredService<IWorkflowOwnedWorkSessionLifecycle>() is DriftingWorkSessions drifting
            ? drifting.Agent
            : (FakeDevWorkflowAgentSession)Services.GetRequiredService<IWorkflowOwnedWorkSessionLifecycle>();

    /// <summary>The second writer, on a host that has one. See <see cref="WithASecondWriter" />.</summary>
    public DriftingWorkSessions Drift => (DriftingWorkSessions)Services.GetRequiredService<IWorkflowOwnedWorkSessionLifecycle>();

    /// <summary>The scripted sandbox. Absent on a host built by <see cref="WithARealSandbox" />, which has the real one.</summary>
    public FakeDevWorkflowToolCommands Tools => (FakeDevWorkflowToolCommands)Services.GetRequiredService<IDevWorkflowToolCommands>();

    /// <summary>
    ///     The scripted development chain, on a host whose construction replaced it. Absent everywhere else — the real
    ///     one needs a repository and a model, so only the implementation-lane tests stand it in.
    /// </summary>
    public FakeDevelopmentTaskChain Chain => (FakeDevelopmentTaskChain)Services.GetRequiredService<IDevelopmentManagementService>();

    /// <summary>The sandbox lane itself, for the one test that has to await a real build rather than sleep on it.</summary>
    public DevWorkflowToolExecutor ToolLane => Services.GetRequiredService<DevWorkflowToolExecutor>();

    /// <summary>
    ///     The dispatcher under test: the container's, or the one a simulated restart replaced it with. Resolved from
    ///     the real container, so its wiring is under test too.
    /// </summary>
    public DevWorkflowDispatcher Dispatcher => _replacement ?? Services.GetRequiredService<DevWorkflowDispatcher>();

    public DevWorkflowGraphCache Graphs => Services.GetRequiredService<DevWorkflowGraphCache>();

    /// <summary>
    ///     A second dispatcher over the same database, standing in for the one a restart would build. It gets a fresh
    ///     graph cache because that is exactly what a restart loses — and losing it must cost nothing but a re-parse.
    /// </summary>
    /// <para>
    ///     Also the only way to run the real signal and sweep pumps: the test host strips every hosted service, so a
    ///     dispatcher the container built is never started.
    /// </para>
    public DevWorkflowDispatcher CreateReplacementDispatcher(bool enabled = true) =>
        new(Services.GetRequiredService<IServiceScopeFactory>(),
            new DevWorkflowGraphCache(),

            // The container's lane, not a second one: the slot count is a property of the NODE, and a restart that
            // handed itself a fresh set of slots would be simulating a machine with twice the sandbox capacity.
            Services.GetRequiredService<DevWorkflowToolExecutor>(),
            Services.GetRequiredService<DevWorkflowRetryPolicy>(),
            Services.GetRequiredService<DevWorkflowMaterializer>(),
            Options.Create(new DevWorkflowOptions
            {
                Enabled = enabled,
                SweepSeconds = Services.GetRequiredService<IOptions<DevWorkflowOptions>>().Value.SweepSeconds
            }),
            Services.GetRequiredService<TimeProvider>(),
            Services.GetRequiredService<ILogger<DevWorkflowDispatcher>>());

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

    /// <summary>Creates a work item, a definition on <paramref name="graphJson" />, and a run pinned to it.</summary>
    public async Task<Guid> StartRunAsync(string graphJson,
        string request = "Explain how the inference path works.",
        Guid? developmentProjectId = null)
    {
        await using var scope = Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
        var workItem = await store.CreateWorkItemAsync(new CreateDevWorkflowWorkItemCommand(Guid.NewGuid(), "Seeded work item", request, developmentProjectId))
                                  .ConfigureAwait(false);
        var definition = await store.CreateDefinitionAsync(new CreateDevWorkflowDefinitionCommand(Guid.NewGuid(), "Seeded definition", graphJson, NodeCount: 1))
                                    .ConfigureAwait(false);
        // The seeds travel with the start, exactly as the run service composes them: a run and its node runs are one
        // commit, so there is no half-started run for a test to accidentally depend on.
        var run = await store.StartRunAsync(new StartDevWorkflowRunCommand(Guid.NewGuid(),
                                 workItem.Id,
                                 definition.Id,
                                 definition.Version,
                                 definition.GraphHash,
                                 graphJson,
                                 await SeedsAsync(store, graphJson, workItem).ConfigureAwait(false)))
                             .ConfigureAwait(false);
        return run.Id;
    }

    /// <summary>
    ///     The node runs a start seeds, or none when the graph cannot be read.
    ///     <para>
    ///         A test that deliberately pins an unroutable graph has no seeds to give — you cannot compose rows from a
    ///         graph you cannot parse — and that is the case under test rather than a hole: the run service refuses such
    ///         a definition long before this point, so the row can only be one created around it, which the dispatcher
    ///         still has to fail cleanly at its first tick.
    ///     </para>
    /// </summary>
    private async Task<IReadOnlyList<DevWorkflowNodeRunSeed>?> SeedsAsync(IDevWorkflowStore store, string graphJson, DevWorkflowWorkItemSnapshot workItem)
    {
        var enabledRuleSets = await store.ListEnabledRuleSetsAsync().ConfigureAwait(false);
        try
        {
            return DevWorkflowRunSeeds.Compose(DevWorkflowGraph.Parse(graphJson),
                workItem,
                inputsJson: null,
                Services.GetRequiredService<IOptions<DevWorkflowOptions>>().Value.MaxNodeRunsPerRun,
                enabledRuleSets);
        }
        catch (DevWorkflowValidationException)
        {
            return null;
        }
    }

    /// <summary>A work item and a definition, without a run: what the run service is handed.</summary>
    public async Task<(Guid WorkItemId, Guid DefinitionId)> SeedDefinitionAsync(string graphJson,
        string request = "Explain how the inference path works.",
        Guid? developmentProjectId = null)
    {
        await using var scope = Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
        var workItem = await store.CreateWorkItemAsync(new CreateDevWorkflowWorkItemCommand(Guid.NewGuid(), "Seeded work item", request, developmentProjectId))
                                  .ConfigureAwait(false);
        var definition = await store.CreateDefinitionAsync(new CreateDevWorkflowDefinitionCommand(Guid.NewGuid(), "Seeded definition", graphJson, NodeCount: 1))
                                    .ConfigureAwait(false);
        return (workItem.Id, definition.Id);
    }

    public async Task ArchiveDefinitionAsync(Guid definitionId)
    {
        await using var scope = Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().ArchiveDefinitionAsync(definitionId).ConfigureAwait(false);
    }

    /// <summary>A rule set the resolver will see, created before the run that has to resolve against it.</summary>
    public async Task<DevWorkflowRuleSetSnapshot> CreateRuleSetAsync(string name, string body, string scopeJson, bool enabled = true)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>()
                          .CreateRuleSetAsync(new CreateDevWorkflowRuleSetCommand(Guid.NewGuid(), name, body, scopeJson, Enabled: enabled))
                          .ConfigureAwait(false);
    }

    /// <summary>Rewrites a rule set the way the PUT endpoint does — whole document, at the version it was read from.</summary>
    public async Task<DevWorkflowRuleSetSnapshot> UpdateRuleSetAsync(Guid ruleSetId, int expectedVersion, string name, string body, string? scopeJson = null)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>()
                          .UpdateRuleSetAsync(new UpdateDevWorkflowRuleSetCommand(ruleSetId,
                              expectedVersion,
                              name,
                              body,
                              scopeJson ?? """{"projectIds":[],"nodeTypes":[]}"""))
                          .ConfigureAwait(false);
    }

    public async Task DeleteRuleSetAsync(Guid ruleSetId)
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().DeleteRuleSetAsync(ruleSetId).ConfigureAwait(false);
    }

    /// <summary>Runs one call against the scoped run service, which is how every endpoint will reach it.</summary>
    public async Task<T> WithRunServiceAsync<T>(Func<IDevWorkflowRunService, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IDevWorkflowRunService>()).ConfigureAwait(false);
    }

    /// <summary>The same, for the one command that answers nothing.</summary>
    public async Task WithRunServiceAsync(Func<IDevWorkflowRunService, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<IDevWorkflowRunService>()).ConfigureAwait(false);
    }

    /// <summary>Whether the work item's row is still there — what a delete has to have removed before it releases anything else.</summary>
    public async Task<bool> WorkItemExistsAsync(Guid workItemId)
    {
        await using var scope = Services.CreateAsyncScope();
        try
        {
            _ = await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().GetWorkItemAsync(workItemId).ConfigureAwait(false);
            return true;
        }
        catch (DevWorkflowNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    ///     A run row with no node runs — the shape nothing in the runtime produces any more, kept so a test can prove
    ///     the dispatcher does not invent the rows (and with them a request nobody made) for one that turns up anyway.
    /// </summary>
    public async Task<Guid> StartRunWithoutNodeRunsAsync(string graphJson)
    {
        await using var scope = Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
        var workItem = await store.CreateWorkItemAsync(new CreateDevWorkflowWorkItemCommand(Guid.NewGuid(), "Seeded work item", "Seeded request")).ConfigureAwait(false);
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

    /// <summary>The work-item LIST row, whose node counters the store computes its own way from the same rows.</summary>
    public async Task<DevWorkflowWorkItemSnapshot> ReadWorkItemRowAsync(Guid workItemId)
    {
        await using var scope = Services.CreateAsyncScope();
        var items = await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().ListWorkItemsAsync().ConfigureAwait(false);
        return items.Single(item => item.Id == workItemId);
    }

    /// <summary>
    ///     Whether the dispatcher was told to look at this run. Draining rather than peeking, because the assertion is
    ///     "the command signalled" and a later one must not read this one's signal.
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

    /// <summary>
    ///     Ticks until quiescent, waits for whatever the sandbox lane is driving to land, and ticks again — repeating
    ///     until a whole pass finds nothing in flight.
    ///     <para>
    ///         Waiting on the lane's own task rather than sleeping, so a test that runs a REAL build is still
    ///         deterministic: it finishes the moment the build does and not a poll interval later.
    ///     </para>
    /// </summary>
    public async Task AdvanceThroughToolLaneAsync(Guid runId, int maxPasses = 12)
    {
        for (var pass = 1; pass <= maxPasses; pass++)
        {
            _ = await AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
            var inFlight = (await ReadNodeRunsAsync(runId).ConfigureAwait(false))
                           .Where(nodeRun => ToolLane.IsInFlight(nodeRun.Id))
                           .ToList();
            if (inFlight.Count == 0)
            {
                return;
            }

            foreach (var nodeRun in inFlight)
            {
                // Bounded, because the failure this catches is a HANG: a lane entry nothing settles — a drain that
                // wrote a terminal over a live row instead of asking it to stop — leaves a pass running that no poll
                // will ever consume, and an unbounded wait would report that as a dead test run rather than a bug.
                await ToolLane.WaitForCompletionAsync(nodeRun.Id)
                              .WaitAsync(TimeSpan.FromMinutes(10))
                              .ConfigureAwait(false);
            }
        }

        throw new AssertionException($"Run {runId} still had sandbox work in flight after {maxPasses} passes.");
    }

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

        // Still writing is a bug UNLESS the sandbox lane is holding a pass: those ticks are real work about a row whose
        // answer has not arrived yet, and waiting for it is AdvanceThroughToolLaneAsync's job — its own maxPasses cap
        // is the hang guard for that case. Throwing here instead made every caller race the lane under load.
        if ((await ReadNodeRunsAsync(runId).ConfigureAwait(false)).Any(nodeRun => ToolLane.IsInFlight(nodeRun.Id)))
        {
            return maxTicks;
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
    public async Task DecideAsync(Guid runId, string nodeKey, DevWorkflowDecisionKind decision, string? subject = "operator", string? comment = null)
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
                           Comment: comment,
                           DecidedBySubject: subject))
                       .ConfigureAwait(false);
    }

    /// <summary>Runs one sweep of every live run, the way the sweep pump does, and answers which runs it advanced.</summary>
    public async Task<int> SweepAsync()
    {
        var before = await Task.WhenAll((await ListRunIdsAsync().ConfigureAwait(false)).Select(ReadRunAsync)).ConfigureAwait(false);
        await Dispatcher.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        var after = await Task.WhenAll(before.Select(run => ReadRunAsync(run.Id))).ConfigureAwait(false);
        return before.Zip(after).Count(pair => pair.First.Version != pair.Second.Version);
    }

    public async Task<IReadOnlyList<Guid>> ListRunIdsAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var runs = await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().ListRunsAsync(limit: 500).ConfigureAwait(false);
        return [.. runs.Select(static run => run.Id)];
    }

    /// <summary>Moves one node run directly, to stand it in a state only a lane this build lacks would produce.</summary>
    public async Task TransitionNodeRunAsync(Guid runId, string nodeKey, DevWorkflowNodeRunStatus target)
    {
        var nodeRun = await ReadNodeRunAsync(runId, nodeKey).ConfigureAwait(false);
        await using var scope = Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>()
                       .TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(runId, nodeRun.Id, DevWorkflowVersions.Any, target))
                       .ConfigureAwait(false);
    }

    /// <summary>
    ///     Waits for the BACKGROUND pump to bring a run to a status. Only for the test that exercises the pump itself —
    ///     everything else drives ticks explicitly, and a wait there would be hiding a race rather than asserting one.
    /// </summary>
    public async Task<DevWorkflowRunSnapshot> WaitForRunStatusAsync(Guid runId, DevWorkflowRunStatus expected, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        DevWorkflowRunSnapshot run;
        do
        {
            run = await ReadRunAsync(runId).ConfigureAwait(false);
            if (run.Status == expected)
            {
                return run;
            }

            await Task.Delay(25).ConfigureAwait(false);
        } while (DateTimeOffset.UtcNow < deadline);

        throw new AssertionException($"Run {runId} was {run.Status}, not {expected}, before the timeout.");
    }

    /// <summary>The work session the node run's current attempt owns.</summary>
    public async Task<Guid> ReadSessionIdAsync(Guid runId, string nodeKey) =>
        (await ReadNodeRunAsync(runId, nodeKey).ConfigureAwait(false)).WorkSessionId
        ?? throw new AssertionException($"Node run '{nodeKey}' of run {runId} owns no work session.");

    /// <summary>Lands the node run's session on a terminal status, which is all "the agent finished" means here.</summary>
    public async Task SettleAgentAsync(Guid runId, string nodeKey, AgentWorkSessionStatus status = AgentWorkSessionStatus.Completed) =>
        _ = await Agent.SettleAsync(await ReadSessionIdAsync(runId, nodeKey).ConfigureAwait(false), status).ConfigureAwait(false);

    /// <summary>Saves an artifact on the node run's session, the way its <c>save_artifact</c> tool would.</summary>
    public async Task<Guid> SaveAgentArtifactAsync(Guid runId, string nodeKey, string name, string content)
    {
        var sessionId = await ReadSessionIdAsync(runId, nodeKey).ConfigureAwait(false);
        var artifactId = Guid.NewGuid();
        await using var scope = Services.CreateAsyncScope();
        var written = await scope.ServiceProvider.GetRequiredService<IWorkSessionArtifactBlobStore>()
                                 .WriteAsync(sessionId, artifactId, Encoding.UTF8.GetBytes(content))
                                 .ConfigureAwait(false);
        _ = await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>()
                       .AppendArtifactAsync(new AppendWorkSessionArtifactCommand(sessionId,
                           artifactId,
                           WorkSessionVersions.Any,
                           Guid.NewGuid(),
                           AgentWorkSessionArtifactKind.Report,
                           name,
                           "text/markdown",
                           written.ContentHash,
                           written.ByteCount,
                           written.OpaqueReference))
                       .ConfigureAwait(false);
        return artifactId;
    }

    /// <summary>Adds one task to the node run's session in the state given, the way its <c>update_work_plan</c> would.</summary>
    public async Task<Guid> ApplyAgentTaskAsync(Guid runId,
        string nodeKey,
        string title,
        AgentWorkSessionTaskStatus status,
        string? blockedReason = null)
    {
        var sessionId = await ReadSessionIdAsync(runId, nodeKey).ConfigureAwait(false);
        var taskId = Guid.NewGuid();
        await using var scope = Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>()
                       .ApplyPlanAsync(new ApplyWorkPlanCommand(sessionId,
                           WorkSessionVersions.Any,
                           Guid.NewGuid(),
                           AgentWorkSessionTaskOrigin.Agent,
                           [new WorkPlanTaskChange(taskId, WorkPlanTaskOperation.Add, Title: title, Status: status, BlockedReason: blockedReason)]))
                       .ConfigureAwait(false);
        return taskId;
    }

    /// <summary>
    ///     Records the completion event the node run's session's <c>complete_work_session</c> tool would, from the
    ///     detail JSON verbatim — so a test can also arrange the shape a build without <c>objectiveMet</c> wrote.
    /// </summary>
    public async Task RequestAgentCompletionAsync(Guid runId, string nodeKey, string detailJson)
    {
        var sessionId = await ReadSessionIdAsync(runId, nodeKey).ConfigureAwait(false);
        await using var scope = Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>()
                       .AppendEventAsync(new AppendWorkSessionEventCommand(sessionId,
                           WorkSessionVersions.Any,
                           WorkSessionEventTypes.CompletionRequested,
                           Guid.NewGuid(),
                           Outcome: null,
                           detailJson))
                       .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DevWorkflowArtifactSnapshot>> ReadArtifactsAsync(Guid runId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().ListArtifactsAsync(runId).ConfigureAwait(false);
    }

    /// <summary>An artifact's stored bytes as text, verified against the row's own digest and size on the way out.</summary>
    public async Task<string> ReadArtifactTextAsync(Guid runId, DevWorkflowArtifactSnapshot artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var read = await Services.GetRequiredService<IDevWorkflowArtifactBlobStore>()
                                 .ReadAsync(runId, artifact.Id, artifact.ContentSha256, artifact.SizeBytes)
                                 .ConfigureAwait(false);
        return read.Status == DevWorkflowArtifactReadStatus.Found
            ? Encoding.UTF8.GetString(read.Content.Span)
            : throw new AssertionException($"Artifact '{artifact.Name}' of run {runId} did not read back: {read.Status}.");
    }

    public async Task<IReadOnlyList<Guid>> ReadConsumedArtifactIdsAsync(Guid runId, string nodeKey)
    {
        var nodeRun = await ReadNodeRunAsync(runId, nodeKey).ConfigureAwait(false);
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().ListConsumedArtifactIdsAsync(nodeRun.Id).ConfigureAwait(false);
    }

    /// <summary>
    ///     A host restart, in the order the composition root registers it: the work-session reconciler terminalizes what
    ///     it was driving, then the workflow one makes its node runs dispatchable again, then a fresh dispatcher takes
    ///     over. Both reconcilers are constructed by hand because the test host strips every hosted service — which is
    ///     why the ORDER is pinned by a registration test rather than observed here.
    ///     <para>
    ///         Everything the harness drives afterwards goes through the replacement, so a test reads like the restart
    ///         it simulates: the same database, and a dispatcher that remembers nothing.
    ///     </para>
    /// </summary>
    public async Task RestartAsync()
    {
        await Dispatcher.DisposeAsync().ConfigureAwait(false);

        var scopes = Services.GetRequiredService<IServiceScopeFactory>();
        await new WorkSessionStartupReconciler(scopes,
                  Services.GetRequiredService<IOptions<WorkSessionOptions>>(),
                  Services.GetRequiredService<ILogger<WorkSessionStartupReconciler>>())
              .StartAsync(CancellationToken.None)
              .ConfigureAwait(false);
        await new DevWorkflowStartupReconciler(scopes,
                  Services.GetRequiredService<IOptions<DevWorkflowOptions>>(),
                  Services.GetRequiredService<ILogger<DevWorkflowStartupReconciler>>())
              .StartAsync(CancellationToken.None)
              .ConfigureAwait(false);

        _replacement = CreateReplacementDispatcher();
    }

    /// <summary>
    ///     A startup recovery that dies before it commits — the one window a restart has: between collapsing the rows
    ///     the dead host stranded and writing what each of them costs.
    ///     <para>
    ///         The transaction is made to fail by handing it a repair under a version the run cannot have, rather than
    ///         by killing a process: what the tests using this assert is the transaction BOUNDARY — that a recovery
    ///         which does not commit leaves nothing behind — and the cause of the failure is not what makes that true.
    ///     </para>
    /// </summary>
    public async Task FailRecoveryAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
        var interrupted = await store.ListInterruptedNodeRunsAsync().ConfigureAwait(false);
        if (interrupted.Count == 0)
        {
            throw new AssertionException("There was no in-flight node run for the failed recovery to have died on.");
        }

        _ = await AssertEx.ThrowsAsync<DevWorkflowConcurrencyException>(() => store.ReconcileNonTerminalNodeRunsAsync("The host restarted.",
                          [
                              .. interrupted.Select(row => new DevWorkflowNodeRunVerdict(row.NodeRunId,
                                  row.Status,
                                  row.Attempt,
                                  row.WorkSessionId,
                                  [
                                      new TransitionDevWorkflowNodeRunCommand(row.RunId,
                                          row.NodeRunId,
                                          long.MaxValue,
                                          DevWorkflowNodeRunStatus.Pending)
                                  ]))
                          ]))
                          .ConfigureAwait(false);
    }

    /// <summary>What a restart would still have to reconcile: the node runs sitting in flight with no executor behind them.</summary>
    public async Task<IReadOnlyList<DevWorkflowReconciledNodeRun>> ReadInterruptedNodeRunsAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().ListInterruptedNodeRunsAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     A development project and the one task it owns, created the way Dev Mode creates them.
    ///     <para>
    ///         The selected folder is registered rather than invented: the project row has a foreign key to it. Its host
    ///         path is never opened, because nothing driven through this harness prepares a workspace — the chain that
    ///         would is the part <see cref="FakeDevelopmentTaskChain" /> scripts.
    ///     </para>
    /// </summary>
    public async Task<(Guid ProjectId, Guid TaskId)> SeedDevelopmentProjectAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var folder = await scope.ServiceProvider.GetRequiredService<ISelectedFolderResolver>()
                                .RegisterAsync(new SelectedFolderRegistration($"devtask-{Guid.NewGuid():N}"[..20],
                                    Path.Combine(Path.GetTempPath(), $"xe-devtask-{Guid.NewGuid():N}")))
                                .ConfigureAwait(false);

        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        _ = await scope.ServiceProvider.GetRequiredService<IDevelopmentStore>()
                       .CreateProjectAsync(new DevelopmentCreateProjectCommand(projectId,
                           taskId,
                           Guid.NewGuid(),
                           "Keep the product working.",
                           Guid.Parse(folder.Id),
                           "repository-identity-hash",
                           "main",
                           "Add the feature",
                           "It has to do the thing.",
                           "[\"it does the thing\"]"))
                       .ConfigureAwait(false);
        return (projectId, taskId);
    }

    public async Task<DevelopmentTaskSnapshot> ReadDevelopmentTaskAsync(Guid taskId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDevelopmentStore>().GetTaskAsync(taskId).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DevelopmentTaskSnapshot>> ListDevelopmentTasksAsync(Guid projectId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDevelopmentStore>().ListTasksAsync(projectId).ConfigureAwait(false);
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

/// <summary>
///     The fake agent session with one thing added: every read of a session re-attempts the node run under test, which
///     is what a second process writing the same database looks like from inside startup recovery.
///     <para>
///         The session read is the hook because recovery does exactly one of them per pass, AFTER it has read the rows
///         it is judging — so every verdict it composes is stale by the time it tries to apply it, on every pass, for
///         as many passes as recovery is willing to take.
///     </para>
/// </summary>
internal sealed class DriftingWorkSessions(FakeDevWorkflowAgentSession agent, IServiceScopeFactory scopes) : IWorkflowOwnedWorkSessionLifecycle
{
    public FakeDevWorkflowAgentSession Agent { get; } = agent;

    /// <summary>The node run to move, or null while the writer is asleep. Set it once the row under test exists.</summary>
    public (Guid RunId, Guid NodeRunId)? Target { get; set; }

    public bool HasCapacity => Agent.HasCapacity;

    public Task<WorkSessionDetail> CreateAsync(string title,
        string objective,
        Guid agentDefinitionId,
        WorkSessionRuntimeOverride? runtime = null,
        CancellationToken cancellationToken = default) =>
        Agent.CreateAsync(title, objective, agentDefinitionId, runtime, cancellationToken);

    public async Task<WorkSessionDetail> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var detail = await Agent.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (Target is not { } target)
        {
            return detail;
        }

        await using var scope = scopes.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>()
                       .TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(target.RunId,
                               target.NodeRunId,
                               DevWorkflowVersions.Any,
                               DevWorkflowNodeRunStatus.Running,
                               IncrementAttempt: true),
                           cancellationToken)
                       .ConfigureAwait(false);
        return detail;
    }

    public Task<WorkSessionDetail> StartAsync(Guid sessionId, WorkSessionRuntimeOverride? runtime = null, CancellationToken cancellationToken = default) =>
        Agent.StartAsync(sessionId, runtime, cancellationToken);

    public Task<WorkSessionDetail> PauseAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Agent.PauseAsync(sessionId, cancellationToken);

    public Task<WorkSessionDetail> ResumeAsync(Guid sessionId, WorkSessionRuntimeOverride? runtime = null, CancellationToken cancellationToken = default) =>
        Agent.ResumeAsync(sessionId, runtime, cancellationToken);

    public Task<WorkSessionDetail> CancelAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Agent.CancelAsync(sessionId, cancellationToken);

    public Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Agent.DeleteAsync(sessionId, cancellationToken);
}
