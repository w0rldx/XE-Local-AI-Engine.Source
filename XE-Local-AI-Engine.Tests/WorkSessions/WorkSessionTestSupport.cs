namespace XE_Local_AI_Engine.Tests.WorkSessions;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Tests.Testing;

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
internal sealed record StepScript(IReadOnlyList<string> EventTypes,
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
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
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
    public static Dictionary<string, string?> Configuration(params (string Key, string Value)[] overrides)
    {
        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["WorkSessions:Enabled"] = "true"
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
    /// </summary>
    public static async Task<AgentWorkSessionSnapshot> SeedSessionAsync(IServiceProvider services,
        Guid sessionId,
        AgentWorkSessionKind kind = AgentWorkSessionKind.Research,
        string objective = "Find out what the knowledge base says about the runtime.")
    {
        await using var scope = services.CreateAsyncScope();
        var conversation = await scope.ServiceProvider.GetRequiredService<INodeChatPersistenceService>()
                                      .CreateConversationAsync(new NodeChatCreateConversationRequest("Seeded session", UserId: null, CreatedAtUtc: 0))
                                      .ConfigureAwait(false);
        var store = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();
        return await store.CreateAsync(new CreateWorkSessionCommand(sessionId, conversation.ConversationId, Guid.NewGuid(), kind, "Seeded session", objective))
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
        }
        while (DateTimeOffset.UtcNow < deadline);

        throw new AssertionException($"Work session {sessionId} was {session.Status}, not {expected}, before the timeout.");
    }
}
