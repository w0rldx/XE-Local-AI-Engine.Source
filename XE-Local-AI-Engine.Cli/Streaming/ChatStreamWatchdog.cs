namespace XE_Local_AI_Engine.Cli.Streaming;

/// <summary>
///     The client-side idle deadline for a chat stream, mirroring the SPA's <c>NodeChatStreamGuard.ts</c>: it only
///     catches a dead transport and never undercuts a wait the node itself allows.
/// </summary>
/// <remarks>
///     First chunk 120 s, then 180 s between frames; 660 s while a local model cold-loads or the turn is parked on a
///     prompt; and never below the node's own turn ceiling plus 30 s once a queued or streaming frame carried it.
/// </remarks>
internal sealed class ChatStreamWatchdog
{
    internal static readonly TimeSpan FirstChunkTimeout = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan InterChunkTimeout = TimeSpan.FromSeconds(180);
    internal static readonly TimeSpan ColdLoadTimeout = TimeSpan.FromSeconds(660);
    internal static readonly TimeSpan ParkedTimeout = TimeSpan.FromSeconds(660);
    internal static readonly TimeSpan InvocationTimeoutGrace = TimeSpan.FromSeconds(30);

    private bool _received;
    private bool _generationStarted;
    private bool _parked;
    private string? _phase;
    private TimeSpan _floor = TimeSpan.Zero;

    /// <summary>True until the first frame arrives; names the watchdog category when it trips.</summary>
    public bool AwaitingFirstFrame => !_received;

    /// <summary>How long to wait for the next frame.</summary>
    public TimeSpan NextDeadline
    {
        get
        {
            if (_parked)
            {
                return _floor > TimeSpan.Zero ? _floor : ParkedTimeout;
            }

            TimeSpan deadline;
            if (!_received)
            {
                deadline = FirstChunkTimeout;
            }
            else if (!_generationStarted && IsColdLoad(_phase))
            {
                deadline = ColdLoadTimeout;
            }
            else
            {
                deadline = InterChunkTimeout;
            }

            return deadline > _floor ? deadline : _floor;
        }
    }

    public void Observe(ChatStreamEvent frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        _received = true;
        if (frame.Type is ChatStreamEventTypes.ApprovalRequested or ChatStreamEventTypes.QuestionRequested)
        {
            _parked = true;
        }
        else if (!KeepsParkArmed(frame))
        {
            _parked = false;
        }

        if (frame.Type == ChatStreamEventTypes.AssistantPhase && frame.RuntimePhase is not null)
        {
            _phase = frame.RuntimePhase;
        }

        if (frame.Type is ChatStreamEventTypes.AssistantQueued or ChatStreamEventTypes.AssistantStreaming
            && frame.InvocationTimeoutSeconds is > 0 and var seconds)
        {
            _floor = TimeSpan.FromSeconds(seconds) + InvocationTimeoutGrace;
        }

        if (string.Equals(_phase, "generating", StringComparison.Ordinal) || !string.IsNullOrEmpty(frame.Delta) || !string.IsNullOrEmpty(frame.ReasoningDelta))
        {
            _generationStarted = true;
        }
    }

    private static bool IsColdLoad(string? phase) =>
        phase is "preparing_runtime" or "loading_model";

    // A resume replays the prompt before its snapshot and phase, and a thinking model sends a whitespace reasoning
    // delta right after the prompt: neither ends the park.
    private static bool KeepsParkArmed(ChatStreamEvent frame) =>
        frame.Type switch
        {
            ChatStreamEventTypes.AssistantSnapshot => true,
            ChatStreamEventTypes.AssistantPhase => !IsColdLoad(frame.RuntimePhase),
            ChatStreamEventTypes.AssistantDelta => string.IsNullOrWhiteSpace(frame.Delta),
            _ => false
        };
}
