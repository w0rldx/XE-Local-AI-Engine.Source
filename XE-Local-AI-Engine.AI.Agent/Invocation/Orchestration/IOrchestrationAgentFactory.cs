namespace XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;

using Microsoft.Extensions.AI;

/// <summary>
///     Builds a handoff orchestration run from a compiled <see cref="OrchestrationAgentDefinition" />, the multi-agent
///     counterpart to <see cref="IInvocationAgentFactory" />.
/// </summary>
/// <remarks>
///     Constructs one agent per participant, reusing the single-agent tool resolution, assembles the MAF handoff
///     <c>Workflow</c>, and returns a drive session exposing a normalized stream. All
///     <c>Microsoft.Agents.AI.Workflows</c> types stay behind this boundary, so <c>.Client.Application</c> remains
///     workflow-type-agnostic.
/// </remarks>
public interface IOrchestrationAgentFactory
{
    /// <summary>
    ///     Builds the participant agents and handoff workflow, and starts a streaming run seeded with
    ///     <paramref name="seed" />, the conversation so far.
    /// </summary>
    /// <remarks>
    ///     The returned session is already started — its first <c>TurnToken</c> has been enqueued — and ready to be
    ///     drained via <see cref="IOrchestrationRunSession.WatchAsync" />.
    /// </remarks>
    Task<IOrchestrationRunSession> CreateAsync(OrchestrationAgentDefinition definition,
        IReadOnlyList<ChatMessage> seed,
        CancellationToken cancellationToken = default);
}
