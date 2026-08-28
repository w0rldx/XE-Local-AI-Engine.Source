namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     A scripted stand-in for the work-session machinery, so the graph can be exercised without a model.
///     <para>
///         It creates REAL session rows and moves their status through the real store rather than holding a status in a
///         field. Everything the runtime reads back — the session's status, its artifacts, its findings — therefore
///         comes from the same rows production reads, and only the part that needs a GPU is replaced: nothing here
///         takes a step.
///     </para>
///     <para>
///         The one interface the runtime's agent lane depends on, and so the one thing a test has to fake. A separate
///         create/poll seam beside this would give the lane two fakes that could disagree about what a session is doing.
///     </para>
/// </summary>
internal sealed class FakeDevWorkflowAgentSession : IWorkflowOwnedWorkSessionLifecycle
{
    private readonly List<Guid> _created = [];
    private readonly IServiceScopeFactory _scopes;

    public FakeDevWorkflowAgentSession(IServiceScopeFactory scopes) =>
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));

    /// <summary>Whether the node's one invocation slot is free. Set false to exercise the queue.</summary>
    public bool HasCapacity { get; set; } = true;

    /// <summary>When set, <see cref="StartAsync" /> refuses the way a lost admission race does.</summary>
    public bool RefuseStart { get; set; }

    /// <summary>When set, <see cref="CreateAsync" /> refuses the way an unusable agent binding does.</summary>
    public string? RefuseCreateWith { get; set; }

    /// <summary>Every session this created, in order.</summary>
    public IReadOnlyList<Guid> Created => [.. _created];

    /// <summary>Every objective it was handed, so a test can assert what the agent was actually asked.</summary>
    public List<string> Objectives { get; } = [];

    /// <summary>Every lifecycle call, in order, as <c>verb</c> against the session it named.</summary>
    public List<(string Verb, Guid SessionId)> Calls { get; } = [];

    public async Task<WorkSessionDetail> CreateAsync(string title, string objective, Guid agentDefinitionId, CancellationToken cancellationToken = default)
    {
        if (RefuseCreateWith is { } refusal)
        {
            throw new WorkSessionValidationException(refusal);
        }

        Objectives.Add(objective);
        await using var scope = _scopes.CreateAsyncScope();

        // A real conversation, because a session owns one and the delete path sweeps it. Nothing here sends a turn on it.
        var conversation = await scope.ServiceProvider.GetRequiredService<INodeChatPersistenceService>()
                                      .CreateConversationAsync(new NodeChatCreateConversationRequest(title, UserId: null, CreatedAtUtc: 0), cancellationToken)
                                      .ConfigureAwait(false);
        var created = await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>()
                                 .CreateAsync(new CreateWorkSessionCommand(Guid.NewGuid(),
                                         conversation.ConversationId,
                                         agentDefinitionId,
                                         AgentWorkSessionKind.Workflow,
                                         title,
                                         objective),
                                     cancellationToken)
                                 .ConfigureAwait(false);
        _created.Add(created.Id);
        Calls.Add(("create", created.Id));
        return ToDetail(created);
    }

    public Task<WorkSessionDetail> GetAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        ReadAsync(sessionId, cancellationToken);

    public Task<WorkSessionDetail> StartAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        MoveAsync("start", sessionId, AgentWorkSessionStatus.Running, cancellationToken);

    public Task<WorkSessionDetail> ResumeAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        MoveAsync("resume", sessionId, AgentWorkSessionStatus.Running, cancellationToken);

    public Task<WorkSessionDetail> PauseAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        MoveAsync("pause", sessionId, AgentWorkSessionStatus.Paused, cancellationToken);

    public Task<WorkSessionDetail> CancelAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        MoveAsync("cancel", sessionId, AgentWorkSessionStatus.Cancelled, cancellationToken);

    public async Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        Calls.Add(("delete", sessionId));
        await using var scope = _scopes.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>().DeleteAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What "the agent finished" looks like from the runtime's side: the session lands on a terminal status.</summary>
    public Task<WorkSessionDetail> SettleAsync(Guid sessionId, AgentWorkSessionStatus status, CancellationToken cancellationToken = default) =>
        MoveAsync("settle", sessionId, status, cancellationToken);

    private async Task<WorkSessionDetail> MoveAsync(string verb, Guid sessionId, AgentWorkSessionStatus target, CancellationToken cancellationToken)
    {
        Calls.Add((verb, sessionId));
        if (RefuseStart && verb is "start" or "resume")
        {
            throw new WorkSessionInvalidTransitionException("The node could not admit the work session just now.");
        }

        await using var scope = _scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();
        var moved = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, WorkSessionVersions.Any, target), cancellationToken)
                               .ConfigureAwait(false);
        return ToDetail(moved);
    }

    private async Task<WorkSessionDetail> ReadAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        return ToDetail(await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>().GetAsync(sessionId, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The step budget is the node's option in production; nothing here reads it, so the default stands in.</summary>
    private static WorkSessionDetail ToDetail(AgentWorkSessionSnapshot session) =>
        new(session.Id,
            session.Title,
            session.Objective,
            session.Kind,
            session.Status,
            session.AgentDefinitionId,
            session.ConversationId,
            session.CurrentTaskId,
            session.StepCount,
            MaxStepsPerRun: 25,
            session.LastCheckpointId,
            session.LastSequence,
            session.Version,
            session.CreatedAtUtc,
            session.UpdatedAtUtc);
}
