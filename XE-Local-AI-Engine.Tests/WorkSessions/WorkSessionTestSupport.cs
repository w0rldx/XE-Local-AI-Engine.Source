namespace XE_Local_AI_Engine.Tests.WorkSessions;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Interfaces;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     One work-session host for a whole test class (<c>ClassDataSource(SharedType.PerClass)</c>), so a class pays the
///     ~1.2 s host build once instead of once per test. It carries the work-session defaults and nothing else — a test
///     that needs a different config value, or its own fake, keeps a private host and says why.
///     <para>
///         The host is shared, so its SQLite database is too: every test that writes must scope what it reads back to
///         its own GUID session id, and none may assert on an absolute row count or on an empty table.
///     </para>
/// </summary>
public sealed class WorkSessionHostFixture : IAsyncInitializer, IAsyncDisposable
{
    public TestServerWebAppFactory Factory { get; } = new()
    {
        AdditionalConfiguration = WorkSessionTestSupport.Configuration()
    };

    public Task InitializeAsync() =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() =>
        Factory.DisposeAsync();
}

/// <summary>
///     <see cref="WorkSessionHostFixture" /> plus the deterministic capability and default-model stubs the service-level
///     suites need — the real resolvers probe installed providers, which would make those assertions depend on the box.
/// </summary>
public sealed class WorkSessionServiceHostFixture : IAsyncInitializer, IAsyncDisposable
{
    public TestServerWebAppFactory Factory { get; } = WorkSessionServiceTests.NewFactory();

    public Task InitializeAsync() =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() =>
        Factory.DisposeAsync();
}

/// <summary>
///     A host whose two work-session personas are already seeded, once, before any test runs.
///     <para>
///         <c>seed_slug</c> carries a filtered unique index, so two tests seeding concurrently on one shared database
///         cannot double-insert — the loser takes a <c>DbUpdateException</c>, which the seeder's best-effort startup
///         contract swallows and logs. That test would then run against a persona it believes was seeded. Seeding once
///         here removes the race; the two tests that exercise seeding itself keep private hosts.
///     </para>
/// </summary>
public sealed class SeededWorkSessionAgentsFixture : IAsyncInitializer, IAsyncDisposable
{
    public TestServerWebAppFactory Factory { get; } = new();

    public async Task InitializeAsync()
    {
        await new WorkSessionAgentSeeder(Factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<WorkSessionAgentSeeder>.Instance)
              .StartAsync(CancellationToken.None)
              .ConfigureAwait(false);

        // StartAsync reports success whether or not it seeded: it catches its own failures by contract, and this
        // fixture hands it a NullLogger, so the warning goes nowhere. Without this check a seeding failure would
        // surface as every persona test asserting on a definition that was never written.
        using var scope = Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
        foreach (var slug in new[]
                 {
                     AgentDefaults.WorkSessionGeneralAgentSeedSlug,
                     AgentDefaults.WorkSessionResearchAgentSeedSlug
                 })
        {
            _ = await store.GetBySeedSlugAsync(slug).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"The work-session agent seeder did not create '{slug}'.");
        }
    }

    public ValueTask DisposeAsync() =>
        Factory.DisposeAsync();
}

/// <summary>
///     One scripted turn: what the fake stream service yields, and what the tools of that turn would have written.
///     <para>
///         <see cref="Park" /> makes the turn stop on an approval and wait to be cancelled, which is the only faithful
///         way to exercise the supervisor's park timeout — the real send path parks by holding the run open until the
///         cancellation registry releases it. <see cref="ParkThenContinue" /> is the answered park: the prompt is
///         emitted and the turn carries straight on through <see cref="EventTypes" />, which is what a human answering
///         the card looks like from the supervisor's side.
///     </para>
/// </summary>
internal sealed record StepScript(
    IReadOnlyList<string> EventTypes,
    Func<IServiceProvider, Guid, Task>? DuringTurn = null,
    bool Park = false,
    bool ParkThenContinue = false,
    string ParkToolName = "ask_user",
    string ParkEventType = ChatStreamEventTypes.ApprovalRequested,
    // Rides on the scripted terminal event. The supervisor reads it to tell a step that spent a BOUND (the
    // provider-call cap, whose message is a fixed constant) from one that actually broke.
    string? TerminalError = null);

/// <summary>
///     Stands in for the chat send path. It records what the supervisor asked for, yields a scripted event sequence, and
///     — like the real service — registers the turn with the cancellation registry so a pause, a cancel or an expired
///     park actually ends it.
///     <para>
///         It also honors <see cref="NodeChatStreamRequest.RefuseUndeclaredWrites" />, by resolving the binding once and
///         asking the SAME production guard the real send asks of its own resolution. That is what makes the supervisor
///         tests here about the supervisor: the decision is not restated, and a turn refused this way never reaches
///         <see cref="Requests" /> because it was never sent. The real service's own single-resolution enforcement is
///         pinned in <c>NodeChatStreamServiceTests</c>.
///     </para>
/// </summary>
internal sealed class FakeNodeChatStreamService(INodeChatStreamCancellationRegistry cancellationRegistry, IServiceProvider services, Guid sessionId)
    : INodeChatStreamService
{
    // Concurrent because the admission test drives several sessions through one fake at once; every other test has a
    // single run and does not care.
    private readonly ConcurrentQueue<StepScript> _scripts = new();
    private readonly Lock _recordGate = new();
    private readonly List<NodeChatStreamRequest> _requests = [];

    /// <summary>
    ///     Every request the supervisor sent, in order. A copy taken under the same lock the recording side holds: the
    ///     admission test drives several runs through one fake, so a test reading the raw list could tear.
    /// </summary>
    public IReadOnlyList<NodeChatStreamRequest> Requests
    {
        get
        {
            lock (_recordGate)
            {
                return [.. _requests];
            }
        }
    }

    public void Enqueue(StepScript script) =>
        _scripts.Enqueue(script);

    public IAsyncEnumerable<ChatStreamEvent> SendMessageAsync(NodeChatStreamRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendCoreAsync(request, cancellationToken);
    }

    private async IAsyncEnumerable<ChatStreamEvent> SendCoreAsync(NodeChatStreamRequest request,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        if (request is { RefuseUndeclaredWrites: true, AgentDefinitionId: { } bound })
        {
            await using var gateScope = services.CreateAsyncScope();
            if (await gateScope.ServiceProvider.GetRequiredService<WorkSessionWriteDeclarationGuard>()
                               .InspectAsync(bound, request.Model, cancellationToken)
                               .ConfigureAwait(false) is { } refusal)
            {
                throw new WorkSessionUndeclaredWriteException(refusal);
            }
        }

        lock (_recordGate)
        {
            _requests.Add(request);
        }

        var script = _scripts.TryDequeue(out var next)
            ? next
            : new StepScript([ChatStreamEventTypes.AssistantCompleted]);

        var correlation = new NodeChatMessageCorrelation(request.ConversationId,
            request.MessageId.GetValueOrDefault(Guid.NewGuid()),
            request.RequestId.GetValueOrDefault(Guid.NewGuid()));
        // Linked so a caller that DOES cancel the enumeration still ends the turn; the supervisor deliberately does not.
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var registration = cancellationRegistry.Register(correlation, turn.Cancel);

        if (script.DuringTurn is { } during)
        {
            await during(services, sessionId).ConfigureAwait(false);
        }

        if (script.ParkThenContinue)
        {
            yield return Event(correlation, script.ParkEventType, script.ParkToolName);
        }

        if (script.Park)
        {
            yield return Event(correlation, script.ParkEventType, script.ParkToolName);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, turn.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The registry released the turn: the run ends Cancelled, exactly as the pump would persist it.
            }

            yield return Event(correlation, ChatStreamEventTypes.AssistantCancelled);
            yield break;
        }

        foreach (var eventType in script.EventTypes)
        {
            yield return Event(correlation, eventType, error: script.TerminalError);
        }
    }

    private static ChatStreamEvent Event(NodeChatMessageCorrelation correlation, string type, string? toolName = null, string? error = null) =>
        new(type,
            correlation.ConversationId,
            correlation.MessageId,
            correlation.RequestId,
            Status: "streaming",
            Sequence: 0,
            OccurredAtUtc: 0,
            Error: error,
            ToolName: toolName,
            ApprovalRequestId: toolName is null ? null : Guid.NewGuid().ToString("N"));
}

/// <summary>Records every publish so a test can assert what the hub would have been told, and when.</summary>
internal sealed class RecordingWorkSessionEventPublisher : IWorkSessionEventPublisher
{
    private readonly Lock _gate = new();
    private readonly List<(Guid SessionId, long Sequence, WorkSessionChangeKind Kind)> _published = [];

    /// <summary>Every publish, in order. A copy taken under the recording lock, for the same reason as above.</summary>
    public IReadOnlyList<(Guid SessionId, long Sequence, WorkSessionChangeKind Kind)> Published
    {
        get
        {
            lock (_gate)
            {
                return [.. _published];
            }
        }
    }

    public Task PublishAsync(Guid sessionId, long sequence, WorkSessionChangeKind kind, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _published.Add((sessionId, sequence, kind));
        }

        return Task.CompletedTask;
    }
}

/// <summary>Shared helpers for the work-session suites: host wiring, seeding, and the waits the loop needs.</summary>
internal static class WorkSessionTestSupport
{
    /// <summary>
    ///     The work-session host defaults, including the operator allow-list the suites' fixture models have to be in.
    ///     <para>
    ///         <c>AgentHome:ToolCapableModels</c> is a SECOND tool gate, independent of the capability probe: the offer
    ///         applies it unconditionally, and a session whose model is missing from it is refused at create and at the
    ///         step boundary. The fixture models are named here for the same reason an operator lists their own model in
    ///         Node Settings — a test that seeded an unlisted model would be testing the refusal, not the path it means
    ///         to exercise. The unlisted cases are deliberate and live in <c>WorkSessionServiceTests</c> (create and
    ///         repoint) and <c>WorkSessionStepLoopTests</c> (the step guard).
    ///     </para>
    /// </summary>
    public static Dictionary<string, string?> Configuration(params (string Key, string Value)[] overrides)
    {
        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["WorkSessions:Enabled"] = "true",
            ["AgentHome:ToolCapableModels:0"] = "tool-capable-model",
            ["AgentHome:ToolCapableModels:1"] = "a-cloud-model",
            ["AgentHome:ToolCapableModels:2"] = "another-local-model"
        };
        foreach (var (key, value) in overrides)
        {
            configuration[key] = value;
        }

        return configuration;
    }

    /// <summary>Registers a shared fake stream service and publisher over the real graph.</summary>
    public static Action<IServiceCollection> WithFakes(Func<IServiceProvider, INodeChatStreamService> streamFactory, RecordingWorkSessionEventPublisher publisher) =>
        services =>
        {
            services.RemoveAll<INodeChatStreamService>();
            services.AddSingleton(streamFactory);
            services.RemoveAll<IWorkSessionEventPublisher>();
            services.AddSingleton<IWorkSessionEventPublisher>(publisher);
        };

    /// <summary>
    ///     Creates a real conversation and a session row bound to it, bypassing the service's agent checks. The
    ///     conversation has to exist: the supervisor refuses to take a step for a session whose conversation is gone.
    ///     <para>
    ///         <paramref name="agentDefinitionId" /> defaults to an id no definition carries, which is also what makes
    ///         the supervisor's tool-gate guard stand down — it judges only sessions whose agent it can still resolve.
    ///         Pass a seeded definition to exercise that guard.
    ///     </para>
    /// </summary>
    public static async Task<AgentWorkSessionSnapshot> SeedSessionAsync(IServiceProvider services,
        Guid sessionId,
        AgentWorkSessionKind kind = AgentWorkSessionKind.Research,
        string objective = "Find out what the knowledge base says about the runtime.",
        Guid? agentDefinitionId = null)
    {
        await using var scope = services.CreateAsyncScope();
        var conversation = await scope.ServiceProvider.GetRequiredService<INodeChatPersistenceService>()
                                      .CreateConversationAsync(new NodeChatCreateConversationRequest("Seeded session", UserId: null, CreatedAtUtc: 0))
                                      .ConfigureAwait(false);
        var store = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();
        return await store.CreateAsync(new CreateWorkSessionCommand(sessionId,
                              conversation.ConversationId,
                              agentDefinitionId ?? Guid.NewGuid(),
                              kind,
                              "Seeded session",
                              objective))
                          .ConfigureAwait(false);
    }

    public static async Task<AgentWorkSessionSnapshot> ReadSessionAsync(IServiceProvider services, Guid sessionId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>().GetAsync(sessionId).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<WorkSessionEventSnapshot>> ReadEventsAsync(IServiceProvider services, Guid sessionId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>().ListEventsAsync(sessionId).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<WorkSessionCheckpointSnapshot>> ReadCheckpointsAsync(IServiceProvider services, Guid sessionId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>().ListCheckpointsAsync(sessionId).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<WorkSessionFindingSnapshot>> ReadFindingsAsync(IServiceProvider services, Guid sessionId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>().ListFindingsAsync(sessionId).ConfigureAwait(false);
    }

    /// <summary>Waits for the supervisor to reach a settled status, so no test asserts against a half-run loop.</summary>
    public static async Task<AgentWorkSessionSnapshot> WaitForStatusAsync(IServiceProvider services,
        Guid sessionId,
        AgentWorkSessionStatus expected,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        AgentWorkSessionSnapshot session;
        do
        {
            session = await ReadSessionAsync(services, sessionId).ConfigureAwait(false);
            if (session.Status == expected)
            {
                return session;
            }

            await Task.Delay(25).ConfigureAwait(false);
        } while (DateTimeOffset.UtcNow < deadline);

        throw new AssertionException($"Work session {sessionId} was {session.Status}, not {expected}, before the timeout.");
    }
}
