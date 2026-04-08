namespace XE_Local_AI_Engine.Models
{
    using System;
    using System.Collections.Generic;
    using XE_Local_AI_Engine.Models.Enums;

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

        public List<string>? RequestedCapabilities { get; init; }

        public required TimeoutSettings Timeouts { get; init; }

        public required string ConfigHash { get; init; }
    }

    public sealed record ConversationMessageDto
    {
        public required Guid Id { get; init; }

        public required MessageRole Role { get; init; }

        public required string Content { get; init; }

        public string? ToolCalls { get; init; }

        public string? ToolResults { get; init; }

        public string? ModelUsed { get; init; }

        public required int SortOrder { get; init; }
    }

    public sealed record AllowedToolDto
    {
        public required Guid Id { get; init; }

        public required string Name { get; init; }

        public required ToolLocation Location { get; init; }

        public string? ParameterSchema { get; init; }
    }

    public sealed record TimeoutSettings
    {
        public int InvocationTimeoutSeconds { get; init; } = 300;

        public int ToolCallTimeoutSeconds { get; init; } = 30;

        public int StreamIdleTimeoutSeconds { get; init; } = 60;
    }
}
