namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

internal sealed record LlamaServerLaunchPlanSet
{
    public required ProcessContextAllocation? Allocation { get; init; }

    public required List<LlamaServerLaunchCandidate> Candidates { get; init; }
}
