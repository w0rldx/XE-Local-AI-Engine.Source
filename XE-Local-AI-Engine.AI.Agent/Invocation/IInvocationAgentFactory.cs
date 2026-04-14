namespace XE_Local_AI_Engine.AI.Agent.Invocation;

public interface IInvocationAgentFactory
{
    Task<InvocationAgentContext> CreateAsync(InvocationAgentDefinition definition, CancellationToken cancellationToken = default);
}
