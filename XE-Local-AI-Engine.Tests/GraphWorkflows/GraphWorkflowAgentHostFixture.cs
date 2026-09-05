namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TUnit.Core.Interfaces;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A graph-workflow host whose agent lane can actually run: the invocation runner is
///     <see cref="FakeGraphWorkflowInvocation" />, and the four seams a unit-test host cannot satisfy — there is no
///     installed GGUF model, no GPU and no model catalog here — answer off the MODEL NAME instead.
///     <para>
///         Name-driven rather than per-test configuration on purpose. The host is shared across a class and TUnit runs
///         its tests in parallel, so a substitute a test reconfigures is state its siblings would read; a fake that
///         answers off the name a test's own graph pins is state nobody shares. See
///         <see cref="GraphWorkflowModels" /> for the names and what each one means.
///     </para>
///     <para>
///         Everything else is the real thing: the store, the dispatcher, the state machine, the package builder, and
///         above all <c>WorkerEventDispatcher</c> with its genuine one-slot invocation semaphore.
///     </para>
/// </summary>
public sealed class GraphWorkflowAgentHostFixture : IAsyncInitializer, IAsyncDisposable
{
    public TestServerWebAppFactory Factory { get; } = NewFactory();

    /// <summary>An agent host of this test's own, for a test that must not share the class's runner script.</summary>
    public static TestServerWebAppFactory NewFactory(params (string Key, string Value)[] configuration) =>
        GraphWorkflowHostFixture.NewFactory(static services =>
            {
                services.RemoveAll<IInvocationRunner>();
                services.AddSingleton<IInvocationRunner>(provider => new FakeGraphWorkflowInvocation(provider));

                services.RemoveAll<ILocalDefaultChatModelResolver>();
                services.AddSingleton<ILocalDefaultChatModelResolver, FakeGraphWorkflowLocalDefaultModel>();

                services.RemoveAll<IModelCapabilityResolver>();
                services.AddSingleton<IModelCapabilityResolver, FakeGraphWorkflowModelCapabilities>();

                services.RemoveAll<ICapacityService>();
                services.AddSingleton<ICapacityService, FakeGraphWorkflowCapacity>();

                services.RemoveAll<IAgentDefinitionResolver>();
                services.AddSingleton<IAgentDefinitionResolver, FakeGraphWorkflowAgentRuntime>();

                // The hub publisher, recorded. A ping is content-free, so what a test can ask of it is that one landed
                // for every committed change — which is what the E2E run asserts alongside its event log.
                services.RemoveAll<IGraphWorkflowEventPublisher>();
                services.AddSingleton<IGraphWorkflowEventPublisher, RecordingGraphWorkflowEventPublisher>();

                // The executor's own logger, so the stripped-offer warning is assertable rather than assumed.
                services.AddSingleton<RecordingLogger<GraphWorkflowAgentExecutor>>();
                services.AddSingleton<ILogger<GraphWorkflowAgentExecutor>>(provider => provider.GetRequiredService<RecordingLogger<GraphWorkflowAgentExecutor>>());
            },

            // The concurrency cap counts LIVE runs across the whole database, and a shared host is a shared database.
            // At the shipped default of four, a class's fifth concurrent run sits Pending behind its siblings rather
            // than because of anything the test did — and an agent test that never dispatches waits out its whole
            // budget. First in the list, so a caller may still pin it. It widens the agent LANE to 64 as well, which is
            // harmless: the node-wide invocation slot stays at one, and that is what the fan-out assertion observes.
            [("GraphWorkflows:MaxConcurrentRuns", "64"), .. configuration]);

    public Task InitializeAsync() =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() =>
        Factory.DisposeAsync();
}

/// <summary>
///     The three MARKERS this module's fakes answer off, and the one concrete name that carries none of them. A graph
///     pinning a model whose name contains a marker is choosing the world its node run wakes up in, which is what lets
///     one shared host serve tests that need different worlds — and lets a test that needs a reservation of its very
///     own simply invent a name.
/// </summary>
internal static class GraphWorkflowModels
{
    /// <summary>The node's local default: node-local, no thinking, no enforceable reasoning budget.</summary>
    public const string LocalDefault = "graph-local-default";

    /// <summary>A name carrying this is cloud-hosted, and an Agent node resolving to it is refused before capacity.</summary>
    public const string CloudMarker = "cloud";

    /// <summary>A name carrying this advertises thinking AND an enforceable reasoning budget.</summary>
    public const string ThinkingMarker = "thinking";

    /// <summary>A name carrying this is more than this node can admit.</summary>
    public const string OvercommittedMarker = "overcommitted";
}

/// <summary>The node's local default, which a unit-test host has no installed GGUF to resolve.</summary>
internal sealed class FakeGraphWorkflowLocalDefaultModel : ILocalDefaultChatModelResolver
{
    public Task<string?> ResolveAsync(string? persistedDefault, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(GraphWorkflowModels.LocalDefault);
}

/// <summary>
///     Capabilities and locality read off the name. <see cref="GraphWorkflowModels.LocalDefault" /> deliberately reports
///     NEITHER thinking nor an enforceable budget: the package builder defaults both to <see langword="true" />, so a
///     package carrying false is proof the executor threaded them rather than letting the default stand.
/// </summary>
internal sealed class FakeGraphWorkflowModelCapabilities : IModelCapabilityResolver
{
    public Task<ModelCapabilitySnapshot> ResolveAsync(string? model, CancellationToken cancellationToken)
    {
        var name = model ?? string.Empty;
        var thinking = name.Contains(GraphWorkflowModels.ThinkingMarker, StringComparison.Ordinal);
        return Task.FromResult(new ModelCapabilitySnapshot(thinking, SupportsTools: true, name.Contains(GraphWorkflowModels.CloudMarker, StringComparison.Ordinal))
        {
            ReasoningBudgetEnforceable = thinking
        });
    }
}

/// <summary>
///     Admission, with every reservation it ever handed out kept by model name — which is how a test proves the lane
///     released one on a path that never got as far as a turn.
/// </summary>
internal sealed class FakeGraphWorkflowCapacity : ICapacityService
{
    /// <summary>The sanitized reason a reject carries. A node run failing for capacity must repeat it verbatim.</summary>
    public const string RejectionReason = "This node does not have enough free memory to load that model.";

    private readonly ConcurrentDictionary<string, ConcurrentQueue<GraphWorkflowReservation>> _reservations = new(StringComparer.Ordinal);

    /// <summary>Every reservation handed out for <paramref name="model" />, in order. A test that wants its own invents a name.</summary>
    public IReadOnlyList<GraphWorkflowReservation> ReservationsFor(string model) =>
        _reservations.TryGetValue(model, out var reservations) ? [.. reservations] : [];

    [SuppressMessage("Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership transfers to the caller, exactly as the real capacity service's does: releasing the reservation "
                        + "on every terminal path is the contract under test, and disposing it here would assert nothing.")]
    public Task<CapacityDecision> DecideAsync(string modelName, ModelRole role, CancellationToken ct)
    {
        if ((modelName ?? string.Empty).Contains(GraphWorkflowModels.OvercommittedMarker, StringComparison.Ordinal))
        {
            return Task.FromResult(new CapacityDecision(CapacityVerdict.RejectInsufficient, RejectionReason, OllamaEvictionWarning: false));
        }

        // One per CALL, not one per model: a test asserting that a path released its reservation is asking about the
        // turn it ran, and a shared instance would answer about somebody else's.
        var reservation = new GraphWorkflowReservation();
        _reservations.GetOrAdd(modelName!, static _ => new ConcurrentQueue<GraphWorkflowReservation>()).Enqueue(reservation);
        return Task.FromResult(new CapacityDecision(CapacityVerdict.Allow, "Capacity available.", OllamaEvictionWarning: false, reservation));
    }
}

/// <summary>A footprint reservation that records how many times it was released. A leaked one rejects later spawns.</summary>
internal sealed class GraphWorkflowReservation : IDisposable
{
    private int _disposals;

    public int Disposals => Volatile.Read(ref _disposals);

    public bool Disposed => Disposals > 0;

    public void Dispose() =>
        _ = Interlocked.Increment(ref _disposals);
}

/// <summary>
///     The agent's resolved runtime, with the arguments it was resolved WITH kept per active model — the only place a
///     test can see the <c>honorModelProfile</c> decision, which leaves no trace on the package.
/// </summary>
internal sealed class FakeGraphWorkflowAgentRuntime : IAgentDefinitionResolver
{
    /// <summary>One approval-required tool and one without, so a stripped offer is visibly a strict subset.</summary>
    public const string ApprovalRequiredTool = "write_file";

    public const string OfferedTool = "read_file";

    private readonly ConcurrentQueue<GraphWorkflowResolveCall> _calls = new();

    public IReadOnlyList<GraphWorkflowResolveCall> Calls =>
        [.. _calls];

    public GraphWorkflowResolveCall CallFor(string activeModelId) =>
        _calls.FirstOrDefault(call => string.Equals(call.ActiveModelId, activeModelId, StringComparison.Ordinal))
        ?? throw new AssertionException($"No agent runtime was resolved against model '{activeModelId}'.");

    public Task<ResolvedAgentRuntime?> ResolveAsync(Guid? agentDefinitionId,
        string? activeModelId,
        string? retrievalQuery = null,
        bool supportsTools = true,
        bool honorModelProfile = true,
        bool activeModelIsCloud = false,
        CancellationToken cancellationToken = default)
    {
        _calls.Enqueue(new GraphWorkflowResolveCall(agentDefinitionId, activeModelId, retrievalQuery, supportsTools, honorModelProfile, activeModelIsCloud));

        return Task.FromResult<ResolvedAgentRuntime?>(new ResolvedAgentRuntime("SCAFFOLD+PERSONA",
            [Tool(OfferedTool, requiresApproval: false), Tool(ApprovalRequiredTool, requiresApproval: true)],

            // The pin, suppressed exactly as the real resolver suppresses it. The executor must bind the effective
            // model regardless, so a test can tell the two apart.
            honorModelProfile ? activeModelId : null,
            "medium",
            AgentDefinitionVersion: 7,
            Guid.Empty,
            "Fake Agent",
            []));
    }

    private static AllowedToolDto Tool(string name, bool requiresApproval) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = ToolLocation.ClientLocal,
            RequiresApproval = requiresApproval
        };
}

/// <summary>One resolve, as the executor asked for it.</summary>
internal sealed record GraphWorkflowResolveCall(Guid? AgentDefinitionId,
    string? ActiveModelId,
    string? RetrievalQuery,
    bool SupportsTools,
    bool HonorModelProfile,
    bool ActiveModelIsCloud);

/// <summary>Every ping the store announced, in the order the commits allocated their watermarks.</summary>
internal sealed class RecordingGraphWorkflowEventPublisher : IGraphWorkflowEventPublisher
{
    private readonly ConcurrentQueue<GraphWorkflowPing> _pings = new();

    public IReadOnlyList<GraphWorkflowPing> PingsFor(Guid runId) =>
        [.. _pings.Where(ping => ping.RunId == runId)];

    public Task PublishAsync(Guid runId, long sequence, GraphWorkflowChangeKind kind, CancellationToken cancellationToken = default)
    {
        _pings.Enqueue(new GraphWorkflowPing(runId, sequence, kind));
        return Task.CompletedTask;
    }
}

internal sealed record GraphWorkflowPing(Guid RunId, long Sequence, GraphWorkflowChangeKind Kind);
