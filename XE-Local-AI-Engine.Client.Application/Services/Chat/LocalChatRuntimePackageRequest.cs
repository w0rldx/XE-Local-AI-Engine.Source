namespace XE_Local_AI_Engine.Client.Services.Chat;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Models;

public sealed class LocalChatRuntimePackageRequest
{
    public required Guid InvocationId { get; init; }

    public required Guid ConversationId { get; init; }

    public required string ResolvedSystemPrompt { get; init; }

    public required IReadOnlyList<ConversationMessageDto> ConversationContext { get; init; }

    public required string? ModelProfile { get; init; }

    public required int AgentDefinitionVersion { get; init; }

    public Guid? ClientNodeId { get; init; }

    public IReadOnlyList<AllowedToolDto>? AllowedTools { get; init; }

    public IReadOnlyDictionary<string, object>? ToolPolicies { get; init; }

    public IReadOnlyList<string>? RequestedCapabilities { get; init; }

    public TimeoutSettings? Timeouts { get; init; }

    public string? ReasoningEffort { get; init; }

    public OrchestrationSpec? OrchestrationSpec { get; init; }

    public bool SupportsThinking { get; init; } = true;

    public SamplingOptions? SamplingOptions { get; init; }

    public IReadOnlyList<ResolvedSkill>? Skills { get; init; }

    public bool IsUnattended { get; init; }

    public IReadOnlyList<ResolvedCustomTool>? CustomTools { get; init; }

    public JsonElement? ResponseJsonSchema { get; init; }

    public bool ReasoningBudgetEnforceable { get; init; } = true;

    public bool DisableToolRelevanceFilter { get; init; }

    public bool AllowAutoModelSwap { get; init; }
}
