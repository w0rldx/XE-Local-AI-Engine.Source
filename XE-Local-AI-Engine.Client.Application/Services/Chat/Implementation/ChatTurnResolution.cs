namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>The up-front per-turn resolution shared by placeholder/variant stamping and runtime-package construction.</summary>
internal sealed record ChatTurnResolution(
    string? ActiveModel,
    string? EffectiveModel,
    ResolvedAgentRuntime? Resolved,
    ResolvedOrchestration? Orchestration,
    bool SupportsThinking,
    bool SupportsTools,
    bool RequiresInstalledChatModel,
    bool ActiveModelIsCloud,
    bool EffectiveModelIsCloud);
