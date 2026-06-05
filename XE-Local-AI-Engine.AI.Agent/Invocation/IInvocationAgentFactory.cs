namespace XE_Local_AI_Engine.AI.Agent.Invocation;

using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;

/// <summary>
///     Builds single-agent execution contexts from runtime-package projections.
/// </summary>
public interface IInvocationAgentFactory
{
    /// <summary>
    ///     Creates an agent, seed messages, run options, and metadata for one invocation without starting the run.
    /// </summary>
    Task<InvocationAgentContext> CreateAsync(InvocationAgentDefinition definition, CancellationToken cancellationToken = default);
}
