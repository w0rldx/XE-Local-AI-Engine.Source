namespace XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;

using Microsoft.Extensions.AI;

/// <summary>
///     Builds a handoff orchestration run from a compiled <see cref="OrchestrationAgentDefinition" />. The
///     multi-agent counterpart to <see cref="IInvocationAgentFactory" />: it constructs one agent per participant
///     (reusing the single-agent tool-resolution), assembles the MAF handoff <c>Workflow</c>, and returns a drive
///     session that exposes a normalized stream. All <c>Microsoft.Agents.AI.Workflows</c> types stay behind this
///     boundary so <c>.Client.Application</c> remains workflow-type-agnostic.
/// </summary>
public interface IOrchestrationAgentFactory
{
    /// <summary>
    ///     Builds the participant agents + handoff workflow and starts a streaming run seeded with
    ///     <paramref name="seed" /> (the conversation so far). The returned session is started (its first
    ///     <c>TurnToken</c> has been enqueued) and ready to be drained via
    ///     <see cref="IOrchestrationRunSession.WatchAsync" />.
    /// </summary>
    Task<IOrchestrationRunSession> CreateAsync(OrchestrationAgentDefinition definition,
        IReadOnlyList<ChatMessage> seed,
        CancellationToken cancellationToken = default);
}
