namespace XE_Local_AI_Engine.Cli.Streaming;

using System.Text;

/// <summary>
///     Folds one turn's hub frames into <see cref="CliStreamEvent" />s, mirroring the SPA's
///     <c>NodeChatStreamTransport.ts</c>: offset-checked deltas, snapshot resets, reconcile and gap repair, terminals.
/// </summary>
/// <remarks>
///     A delta that does not start where the text ends asks for a resume through <c>ResumeMessage</c>, whose first frame
///     is a snapshot; a delta wholly behind both positions is a replayed duplicate and is dropped. A prompt replayed by
///     a resume is surfaced once. After a terminal frame every later frame is dropped.
/// </remarks>
internal sealed class ChatStreamFolder
{
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _reasoning = new();
    private readonly HashSet<string> _surfacedPrompts = new(StringComparer.Ordinal);
    private long? _nextContentOffset;
    private long? _nextReasoningOffset;

    public ChatStreamFolder(Guid? conversationId = null, Guid? messageId = null, Guid? invocationId = null)
    {
        ConversationId = conversationId;
        MessageId = messageId;
        InvocationId = invocationId;
    }

    public Guid? ConversationId { get; private set; }

    /// <summary>The assistant message id: known up front for a send, else latched from the first frame or set by the caller.</summary>
    public Guid? MessageId { get; set; }

    public Guid? InvocationId { get; private set; }

    public bool IsTerminal { get; private set; }

    public string Content => _content.ToString();

    public string Reasoning => _reasoning.ToString();

    public FoldOutcome Fold(ChatStreamEvent frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (IsTerminal)
        {
            return FoldOutcome.Dropped;
        }

        ConversationId ??= NonEmpty(frame.ConversationId);
        InvocationId ??= NonEmpty(frame.RequestId);
        MessageId ??= NonEmpty(frame.MessageId);

        return frame.Type switch
        {
            ChatStreamEventTypes.AssistantReconcile => FoldOutcome.Resume,
            ChatStreamEventTypes.AssistantDelta => FoldDelta(frame),
            ChatStreamEventTypes.AssistantSnapshot => Replace(frame, CliStreamEventKind.Snapshot),
            ChatStreamEventTypes.AssistantCompleted => Finish(frame, CliStreamEventKind.Completed),
            ChatStreamEventTypes.AssistantCancelled => Finish(frame, CliStreamEventKind.Cancelled),
            ChatStreamEventTypes.AssistantFailed => Finish(frame, CliStreamEventKind.Failed),
            ChatStreamEventTypes.AssistantInterrupted => Finish(frame, CliStreamEventKind.Interrupted),
            ChatStreamEventTypes.ApprovalRequested => SurfaceOnce(frame, "approval:" + (frame.ApprovalRequestId ?? frame.ToolCallId), CliStreamEventKind.ApprovalRequested),
            ChatStreamEventTypes.QuestionRequested => SurfaceOnce(frame, "question:" + frame.QuestionRequestId, CliStreamEventKind.QuestionRequested),
            ChatStreamEventTypes.ToolCallRequested or ChatStreamEventTypes.ToolCallCompleted => Emit(frame, CliStreamEventKind.ToolActivity),
            ChatStreamEventTypes.AssistantNotice => Emit(frame, CliStreamEventKind.Notice, frame.NoticeMessage ?? string.Empty),

            // Persisted, pending, queued, streaming, phase, and any type a newer node adds: progress, never a silent drop.
            _ => Emit(frame, CliStreamEventKind.Status)
        };
    }

    private static Guid? NonEmpty(Guid value) =>
        value == Guid.Empty ? null : value;

    private static bool OnTrack(long offset, long? expected) =>
        expected is null || offset == expected;

    private static bool Behind(long offset, int length, long? expected) =>
        expected is { } next && offset < next && offset + length <= next;

    private FoldOutcome FoldDelta(ChatStreamEvent frame)
    {
        var delta = frame.Delta ?? string.Empty;
        var reasoningDelta = frame.ReasoningDelta ?? string.Empty;
        var contentOffset = frame.ContentOffset ?? 0;
        var reasoningOffset = frame.ReasoningOffset ?? 0;
        var contentOnTrack = OnTrack(contentOffset, _nextContentOffset);
        var reasoningOnTrack = OnTrack(reasoningOffset, _nextReasoningOffset);
        if (!contentOnTrack || !reasoningOnTrack)
        {
            var contentReplayed = Behind(contentOffset, delta.Length, _nextContentOffset) || (contentOnTrack && delta.Length == 0);
            var reasoningReplayed = Behind(reasoningOffset, reasoningDelta.Length, _nextReasoningOffset) || (reasoningOnTrack && reasoningDelta.Length == 0);
            return contentReplayed && reasoningReplayed ? FoldOutcome.Dropped : FoldOutcome.Resume;
        }

        _content.Append(delta);
        _reasoning.Append(reasoningDelta);
        _nextContentOffset = contentOffset + delta.Length;
        _nextReasoningOffset = reasoningOffset + reasoningDelta.Length;
        return new FoldOutcome(ToEvent(frame, CliStreamEventKind.Delta, delta, reasoningDelta), NeedsResume: false);
    }

    private FoldOutcome Replace(ChatStreamEvent frame, CliStreamEventKind kind)
    {
        _content.Clear().Append(frame.Content);
        _reasoning.Clear().Append(frame.Reasoning);
        _nextContentOffset = _content.Length;
        _nextReasoningOffset = _reasoning.Length;
        return new FoldOutcome(ToEvent(frame, kind, Content, Reasoning), NeedsResume: false);
    }

    // A terminal carries the full text; a field the node leaves out keeps its folded text.
    private FoldOutcome Finish(ChatStreamEvent frame, CliStreamEventKind kind)
    {
        IsTerminal = true;
        if (frame.Content is not null)
        {
            _content.Clear().Append(frame.Content);
        }

        if (frame.Reasoning is not null)
        {
            _reasoning.Clear().Append(frame.Reasoning);
        }

        return new FoldOutcome(ToEvent(frame, kind, Content, Reasoning), NeedsResume: false);
    }

    private FoldOutcome SurfaceOnce(ChatStreamEvent frame, string promptKey, CliStreamEventKind kind) =>
        _surfacedPrompts.Add(promptKey) ? Emit(frame, kind) : FoldOutcome.Dropped;

    private FoldOutcome Emit(ChatStreamEvent frame, CliStreamEventKind kind, string text = "") =>
        new(ToEvent(frame, kind, text, string.Empty), NeedsResume: false);

    private CliStreamEvent ToEvent(ChatStreamEvent frame, CliStreamEventKind kind, string text, string reasoning) =>
        new()
        {
            Kind = kind,
            ConversationId = ConversationId ?? frame.ConversationId,
            MessageId = MessageId ?? frame.MessageId,
            RequestId = InvocationId ?? frame.RequestId,
            Text = text,
            Reasoning = reasoning,
            Source = frame
        };

    /// <summary>The event to surface (null when the frame was consumed), and whether the stream must resume to repair.</summary>
    internal readonly record struct FoldOutcome(CliStreamEvent? Event, bool NeedsResume)
    {
        public static FoldOutcome Dropped => new(Event: null, NeedsResume: false);

        public static FoldOutcome Resume => new(Event: null, NeedsResume: true);
    }
}
