namespace XE_Local_AI_Engine.Client.Models;

public sealed record RuntimePackage
{
    public required Guid InvocationId { get; init; }

    public required Guid ConversationId { get; init; }

    public required Guid ClientNodeId { get; init; }

    public required int AgentDefinitionVersion { get; init; }

    public required string ResolvedSystemPrompt { get; init; }

    public required List<ConversationMessageDto> ConversationContext { get; init; }

    public required List<AllowedToolDto> AllowedTools { get; init; }

    public Dictionary<string, object>? ToolPolicies { get; init; }

    public string? ModelProfile { get; init; }

    public string? ReasoningEffort { get; init; }

    public List<string>? RequestedCapabilities { get; init; }

    public required TimeoutSettings Timeouts { get; init; }

    public required string ConfigHash { get; init; }
}
