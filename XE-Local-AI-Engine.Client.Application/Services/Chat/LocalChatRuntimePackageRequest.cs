namespace XE_Local_AI_Engine.Client.Services.Chat;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Models;

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
    OrchestrationSpec? OrchestrationSpec = null,
    bool SupportsThinking = true,
    SamplingOptions? SamplingOptions = null,
    IReadOnlyList<ResolvedSkill>? Skills = null,
    bool IsUnattended = false,
    IReadOnlyList<ResolvedCustomTool>? CustomTools = null,
    JsonElement? ResponseJsonSchema = null,
    bool ReasoningBudgetEnforceable = true,
    bool DisableToolRelevanceFilter = false);
