namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Agents;

public interface IBenchmarkEligibilityPolicy
{
    ResolvedAgentRuntime Apply(ResolvedAgentRuntime runtime);
}
