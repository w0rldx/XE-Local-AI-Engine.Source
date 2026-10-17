namespace XE_Local_AI_Engine.Cli.Rendering;

using XE_Local_AI_Engine.Cli.Api;

// The --json output contract. Every member is always written (null when absent), so a script can rely on the key set;
// wiki 11, "Terminal client (xe)", documents it and AskCommandTests pins it.

/// <summary>One <c>xe ask --json</c> line: a stream event (<c>type</c> "event") or a client notice (<c>type</c> "notice").</summary>
internal sealed class AskJsonLine
{
    public required string Type { get; init; }

    /// <summary>The event kind in camelCase (<c>delta</c>, <c>toolActivity</c>, ...) or the client notice kind.</summary>
    public required string Kind { get; init; }

    /// <summary>The hub frame type the event came from, such as <c>tool-call-completed</c>; null on a client notice.</summary>
    public string? FrameType { get; init; }

    public Guid? ConversationId { get; init; }

    public Guid? MessageId { get; init; }

    public Guid? RequestId { get; init; }

    public string? Status { get; init; }

    public string Text { get; init; } = string.Empty;

    public string Reasoning { get; init; } = string.Empty;

    public string? ToolCallId { get; init; }

    public string? ToolName { get; init; }

    public string? Arguments { get; init; }

    public string? Result { get; init; }

    public bool? IsError { get; init; }

    public string? ApprovalRequestId { get; init; }

    public string? QuestionRequestId { get; init; }

    /// <summary>The <c>ask_user</c> questions as the node's JSON text.</summary>
    public string? Questions { get; init; }

    public bool? SessionScopeEligible { get; init; }

    public string? NoticeKind { get; init; }

    public string? Error { get; init; }

    public string? Model { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }
}

/// <summary>The last <c>xe ask --json</c> line (<c>type</c> "summary").</summary>
internal sealed class AskJsonSummary
{
    public string Type { get; init; } = "summary";

    /// <summary><c>completed</c>, <c>cancelled</c>, <c>failed</c>, <c>interrupted</c> or <c>none</c> (the stream ended without a terminal).</summary>
    public required string Outcome { get; init; }

    public required int ExitCode { get; init; }

    public bool UserCancelled { get; init; }

    public bool InteractionRequired { get; init; }

    public Guid? ConversationId { get; init; }

    public Guid? MessageId { get; init; }

    public Guid? RequestId { get; init; }

    public string Text { get; init; } = string.Empty;

    public string Reasoning { get; init; } = string.Empty;

    public string? Error { get; init; }

    public string? Model { get; init; }
}

/// <summary>What a <c>--json</c> command writes to standard output when it fails before or instead of its result.</summary>
internal sealed class CliJsonError
{
    public string Type { get; init; } = "error";

    public required int ExitCode { get; init; }

    public required string Message { get; init; }
}

/// <summary><c>xe status --json</c>.</summary>
internal sealed class StatusJson
{
    public required string Origin { get; init; }

    public int? ProcessId { get; init; }

    public string? DataDirectory { get; init; }

    public bool SetupRequired { get; init; }

    public required string Vault { get; init; }

    public required NodeInfo Node { get; init; }
}

/// <summary>One <c>xe models --json</c> element: an installed model, or a running one the installed list does not name.</summary>
internal sealed class ModelJson
{
    public required string ModelName { get; init; }

    public string? Provider { get; init; }

    public string? Kind { get; init; }

    public string? ParameterSize { get; init; }

    public string? QuantizationLevel { get; init; }

    public long? SizeBytes { get; init; }

    public bool IsSelected { get; init; }

    public bool IsReasoningCapable { get; init; }

    public bool IsToolCapable { get; init; }

    public bool Installed { get; init; }

    public bool Running { get; init; }

    public long? SizeVramBytes { get; init; }
}
