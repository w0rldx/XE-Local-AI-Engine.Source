namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

internal sealed record LlamaServerLaunchCandidate
{
    public required ResolvedLaunchArguments Resolved { get; init; }

    public required LlamaServerLaunchPlan? Plan { get; init; }

    public required LlamaServerLoadAttemptKind AttemptKind { get; init; }
}
