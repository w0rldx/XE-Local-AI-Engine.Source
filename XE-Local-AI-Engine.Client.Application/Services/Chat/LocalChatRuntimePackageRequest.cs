namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Request DTO for local chat runtime package operations.
/// </summary>
public sealed record LocalChatRuntimePackageRequest(
    Guid InvocationId,
    Guid ConversationId,
    string ResolvedSystemPrompt,
    IReadOnlyList<ConversationMessageDto> ConversationContext,
    string? ModelProfile,
    int AgentDefinitionVersion,
    Guid? ClientNodeId = null,
    IReadOnlyList<AllowedToolDto>? AllowedTools = null,
    IReadOnlyDictionary<string, object>? ToolPolicies = null,
    IReadOnlyList<string>? RequestedCapabilities = null,
    TimeoutSettings? Timeouts = null,
    string? ReasoningEffort = null,
    OrchestrationSpec? OrchestrationSpec = null);
