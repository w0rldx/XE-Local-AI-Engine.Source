namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;
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
    ///     The test host removes every hosted service, so the dispatcher's signal and sweep pumps never run here and
    ///     every advance is one a test asked for. The sweep interval is set past any test's lifetime anyway, so this
    ///     stays deterministic if that ever changes — but the pumps themselves are covered by unit-level assertions on
    ///     <c>AdvanceSafelyAsync</c> rather than by this harness.
    /// </summary>
    private readonly TestServerWebAppFactory _factory;
    private DevWorkflowDispatcher? _replacement;

    public DevWorkflowHarness(params (string Key, string Value)[] configuration)
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

        _factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = settings,

            // The agent lane's one seam, replaced wholesale: everything else — the store, the blob stores, the graph,
            // the dispatcher — is the real thing, and only the part that would need a model is scripted.
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IWorkflowOwnedWorkSessionLifecycle>();
                services.AddSingleton<IWorkflowOwnedWorkSessionLifecycle>(provider =>
                    new FakeDevWorkflowAgentSession(provider.GetRequiredService<IServiceScopeFactory>()));
            }
        };
    }

    public IServiceProvider Services => _factory.Services;

    /// <summary>
    ///     The scripted agent. Resolving the seam is what builds it, because a test scripts the agent BEFORE the first
    ///     tick asks the container for one.
    /// </summary>
    public FakeDevWorkflowAgentSession Agent => (FakeDevWorkflowAgentSession)Services.GetRequiredService<IWorkflowOwnedWorkSessionLifecycle>();

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

        await _factory.DisposeAsync().ConfigureAwait(false);
    }

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

    /// <summary>Runs one call against the scoped run service, which is how every endpoint will reach it.</summary>
    public async Task<T> WithRunServiceAsync<T>(Func<IDevWorkflowRunService, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IDevWorkflowRunService>()).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<DevWorkflowArtifactSnapshot>> ReadArtifactsAsync(Guid runId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().ListArtifactsAsync(runId).ConfigureAwait(false);
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

    /// <summary>Moves the run itself, the way the run service's fire-and-forget commands will.</summary>
    public async Task TransitionRunAsync(Guid runId, DevWorkflowRunStatus target)
    {
        await using var scope = Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>()
                       .TransitionRunAsync(new TransitionDevWorkflowRunCommand(runId, DevWorkflowVersions.Any, target))
                       .ConfigureAwait(false);
    }
}
