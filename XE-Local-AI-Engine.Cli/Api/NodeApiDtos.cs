namespace XE_Local_AI_Engine.Cli.Api;

// Hand-declared from client-react/openapi/v1.json, only the members the CLI reads. Read back from the node, so no
// member is `required` (wiki 16, "DTOs, records and type choice"); NodeApiDtoContractTests pins the names.

/// <summary><c>GET chat/conversations</c>.</summary>
internal sealed class ConversationList
{
    public IReadOnlyList<ConversationSummary> Items { get; init; } = [];

    public int MaxMessageSizeKb { get; init; }
}

internal sealed class ConversationSummary
{
    public Guid ConversationId { get; init; }

    public string? Title { get; init; }

    public long CreatedAtUtc { get; init; }

    public long LastSeenUtc { get; init; }

    public string? LastMessagePreview { get; init; }

    public string? LastMessageStatus { get; init; }

    public string Origin { get; init; } = string.Empty;

    public bool IsPinned { get; init; }

    public bool Archived { get; init; }
}

/// <summary><c>POST chat/conversations</c>.</summary>
internal sealed class CreateConversationRequest
{
    public string? Title { get; init; }

    public Guid? AgentDefinitionId { get; init; }
}

/// <summary><c>GET chat/conversations/{id}</c> and the create response.</summary>
internal sealed class Conversation
{
    public Guid ConversationId { get; init; }

    public string? Title { get; init; }

    public Guid? AgentDefinitionId { get; init; }

    public bool Archived { get; init; }

    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];
}

internal sealed class ChatMessage
{
    public Guid MessageId { get; init; }

    public Guid ConversationId { get; init; }

    public Guid? RequestId { get; init; }

    public long Sequence { get; init; }

    public string Role { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public string? Reasoning { get; init; }

    public string Status { get; init; } = string.Empty;

    public string? Model { get; init; }

    public string? Error { get; init; }

    public string? AgentName { get; init; }

    public long CreatedAtUtc { get; init; }
}

/// <summary><c>POST chat/cancel</c>.</summary>
internal sealed class CancelMessageRequest
{
    public Guid ConversationId { get; init; }

    public Guid MessageId { get; init; }

    public Guid RequestId { get; init; }
}

internal sealed class CancelMessageResponse
{
    public string Status { get; init; } = string.Empty;

    public bool Cancelled { get; init; }
}

/// <summary><c>POST chat/approvals/resolve</c>; <see cref="Scope" /> is <c>Once</c>, <c>Session</c> or null.</summary>
internal sealed class ResolveApprovalRequest
{
    public string RequestId { get; init; } = string.Empty;

    public bool Approved { get; init; }

    public string? Scope { get; init; }
}

/// <summary><c>POST chat/questions/resolve</c>.</summary>
internal sealed class ResolveQuestionRequest
{
    public string RequestId { get; init; } = string.Empty;

    public IReadOnlyList<QuestionAnswer> Answers { get; init; } = [];
}

internal sealed class QuestionAnswer
{
    public string Question { get; init; } = string.Empty;

    public IReadOnlyList<string> Selected { get; init; } = [];

    public string? Other { get; init; }
}

/// <summary><c>GET models</c>.</summary>
internal sealed class LocalModelList
{
    public bool IsAvailable { get; init; }

    public string? SelectedModelName { get; init; }

    public string? ConfiguredDefaultModelName { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<LocalModel> Items { get; init; } = [];
}

internal sealed class LocalModel
{
    public string ModelName { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public string? DisplayLabel { get; init; }

    public long? SizeBytes { get; init; }

    public string? ParameterSize { get; init; }

    public string? QuantizationLevel { get; init; }

    public bool IsSelected { get; init; }

    public string Kind { get; init; } = string.Empty;

    public IReadOnlyList<string> Capabilities { get; init; } = [];

    public bool IsReasoningCapable { get; init; }

    public bool IsToolCapable { get; init; }
}

/// <summary><c>GET models/running</c>.</summary>
internal sealed class RunningModelList
{
    public bool IsAvailable { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<RunningModel> Items { get; init; } = [];
}

internal sealed class RunningModel
{
    public string ModelName { get; init; } = string.Empty;

    public long? SizeBytes { get; init; }

    public long? SizeVramBytes { get; init; }

    public long? ExpiresAtUtc { get; init; }
}

/// <summary><c>GET agents</c>.</summary>
internal sealed class AgentDefinitionList
{
    public IReadOnlyList<AgentDefinition> Items { get; init; } = [];
}

internal sealed class AgentDefinition
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string Kind { get; init; } = string.Empty;

    public IReadOnlyList<string> AllowedToolNames { get; init; } = [];

    public bool IsDefaultAssistant { get; init; }
}

/// <summary><c>GET diagnostics/node-info</c>.</summary>
internal sealed class NodeInfo
{
    public string Version { get; init; } = string.Empty;

    public string? Commit { get; init; }

    public string Flavour { get; init; } = string.Empty;

    public string OsDescription { get; init; } = string.Empty;

    public string? GpuVendor { get; init; }

    public string? InferenceBackend { get; init; }

    public long UptimeSeconds { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
