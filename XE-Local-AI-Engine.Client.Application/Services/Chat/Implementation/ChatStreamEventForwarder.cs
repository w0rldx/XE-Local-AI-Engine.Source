namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Threading.Channels;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>
///     Fans the worker dispatcher's per-invocation events into ONE turn's stream: invocation-state snapshots go to the
///     pump's channel, tool-call / turn-notice / approval / question payloads go straight to the SSE sink. Every handler
///     filters on the turn's own <c>requestId</c>, so a concurrent turn's events are never mixed in.
///     <para>
///         Ordered <c>parts[]</c> accumulation happens HERE for the tool-call and turn-notice payloads (they are part of
///         the reload render source), and deliberately NOT for approvals or questions: those are transient live state
///         that the loopback resolve endpoint clears, and a reloaded terminal turn shows the executed/rejected tool
///         result rather than a lingering prompt. A question still pending when the browser reconnects is replayed from
///         <c>InvocationState.PendingQuestion</c> by the resume registry.
///     </para>
///     <para>
///         Subscription happens in the constructor so it covers pre-run notice production (cloud attachment/knowledge
///         withholding) and every pre-ownership exit — a staging or package-construction failure cannot leak handlers.
///         <see cref="Dispose" /> is idempotent: the caller unsubscribes explicitly AFTER draining the run tasks (the
///         runner may fire its Completed terminal after the SSE loop exits), and the enclosing <c>using</c> is only the
///         safety net for the early-exit paths.
///     </para>
/// </summary>
internal sealed class ChatStreamEventForwarder : IDisposable
{
    private readonly NodeChatMessageCorrelation _correlation;
    private readonly IWorkerEventDispatcher _dispatcher;
    private readonly IChatStreamEventSink _eventSink;
    private readonly NodeChatPartAccumulator _parts;
    private readonly Guid _requestId;
    private readonly NodeChatStreamSequence _sequence;
    private readonly ChannelWriter<InvocationState> _stateWriter;
    private readonly TimeProvider _timeProvider;

    private int _disposed;

    public ChatStreamEventForwarder(IWorkerEventDispatcher dispatcher,
        NodeChatMessageCorrelation correlation,
        Guid requestId,
        ChannelWriter<InvocationState> stateWriter,
        IChatStreamEventSink eventSink,
        NodeChatStreamSequence sequence,
        NodeChatPartAccumulator parts,
        TimeProvider timeProvider)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _requestId = requestId;
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
        _sequence = sequence ?? throw new ArgumentNullException(nameof(sequence));
        _parts = parts ?? throw new ArgumentNullException(nameof(parts));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        _dispatcher.InvocationStateChanged += OnInvocationStateChanged;
        _dispatcher.ToolCallLifecycleChanged += OnToolCallLifecycleChanged;
        _dispatcher.TurnNoticeChanged += OnTurnNoticeChanged;
        _dispatcher.ApprovalRequestedChanged += OnApprovalRequestedChanged;
        _dispatcher.UserQuestionRequestedChanged += OnUserQuestionRequestedChanged;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _dispatcher.InvocationStateChanged -= OnInvocationStateChanged;
        _dispatcher.ToolCallLifecycleChanged -= OnToolCallLifecycleChanged;
        _dispatcher.TurnNoticeChanged -= OnTurnNoticeChanged;
        _dispatcher.ApprovalRequestedChanged -= OnApprovalRequestedChanged;
        _dispatcher.UserQuestionRequestedChanged -= OnUserQuestionRequestedChanged;
    }

    private void OnInvocationStateChanged(object? sender, InvocationStateChangedEventArgs args)
    {
        if (args.State.InvocationId == _requestId)
        {
            _stateWriter.TryWrite(args.State);
        }
    }

    private void OnToolCallLifecycleChanged(object? sender, ToolCallLifecycleChangedEventArgs args)
    {
        if (args.Payload.InvocationId == _requestId)
        {
            var toolSequence = _sequence.Next();
            ChatStreamEventMapper.AccumulateToolPart(_parts, args.Payload, toolSequence);
            _eventSink.TryWrite(ChatStreamEventMapper.ToolCallEvent(_correlation.ConversationId, _correlation.MessageId, _correlation.RequestId, args.Payload, NowUnixMilliseconds(),
                toolSequence));
        }
    }

    private void OnTurnNoticeChanged(object? sender, TurnNoticeChangedEventArgs args)
    {
        if (args.Payload.InvocationId == _requestId)
        {
            var noticeSequence = _sequence.Next();
            ChatStreamEventMapper.AccumulateNotice(_parts, args.Payload, noticeSequence);
            _eventSink.TryWrite(ChatStreamEventMapper.NoticeEvent(_correlation.ConversationId, _correlation.MessageId, _correlation.RequestId, args.Payload, NowUnixMilliseconds(),
                noticeSequence));
        }
    }

    private void OnApprovalRequestedChanged(object? sender, ApprovalRequestedChangedEventArgs args)
    {
        if (args.Payload.InvocationId == _requestId)
        {
            _eventSink.TryWrite(ChatStreamEventMapper.ApprovalRequestedEvent(_correlation.ConversationId, _correlation.MessageId, _correlation.RequestId, args.Payload,
                NowUnixMilliseconds(), _sequence.Next()));
        }
    }

    private void OnUserQuestionRequestedChanged(object? sender, UserQuestionRequestedChangedEventArgs args)
    {
        if (args.Payload.InvocationId == _requestId)
        {
            _eventSink.TryWrite(ChatStreamEventMapper.QuestionRequestedEvent(_correlation.ConversationId, _correlation.MessageId, _correlation.RequestId, args.Payload,
                NowUnixMilliseconds(), _sequence.Next()));
        }
    }

    private long NowUnixMilliseconds()
    {
        return _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }
}
