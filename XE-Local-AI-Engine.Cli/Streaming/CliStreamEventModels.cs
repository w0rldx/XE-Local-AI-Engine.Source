namespace XE_Local_AI_Engine.Cli.Streaming;

/// <summary>What a folded hub frame means to the terminal.</summary>
internal enum CliStreamEventKind
{
    /// <summary>Progress with no text: persisted, pending, queued, streaming, or a runtime phase.</summary>
    Status,

    /// <summary>New content and/or reasoning text to append.</summary>
    Delta,

    /// <summary>The authoritative text so far, replacing what was shown (after a resume or a repair).</summary>
    Snapshot,

    /// <summary>A tool call was requested or completed.</summary>
    ToolActivity,

    /// <summary>The turn is parked on a tool approval: resolve it with the source's <c>ApprovalRequestId</c>.</summary>
    ApprovalRequested,

    /// <summary>The turn is parked on an <c>ask_user</c> question: answer with the source's <c>QuestionRequestId</c>.</summary>
    QuestionRequested,

    /// <summary>A non-fatal notice (model substitution, tool disabled, history truncated).</summary>
    Notice,

    Completed,

    Cancelled,

    Failed,

    Interrupted
}

/// <summary>One event of a CLI chat stream: its kind, the turn's ids and the text to render.</summary>
internal sealed class CliStreamEvent
{
    public required CliStreamEventKind Kind { get; init; }

    public required Guid ConversationId { get; init; }

    /// <summary>The assistant message the turn renders into; a resumed frame's invocation id is mapped back onto it.</summary>
    public required Guid MessageId { get; init; }

    /// <summary>The invocation id; <c>chat/cancel</c> calls it the request id.</summary>
    public required Guid RequestId { get; init; }

    /// <summary>Delta: the new content. Snapshot and terminal kinds: all content so far. Notice: the message.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Delta: the new reasoning. Snapshot and terminal kinds: all reasoning so far.</summary>
    public string Reasoning { get; init; } = string.Empty;

    /// <summary>The hub frame this event came from, for the fields a kind carries (tool name, prompt ids, error).</summary>
    public required ChatStreamEvent Source { get; init; }
}
