namespace XE_Local_AI_Engine.Cli.Streaming;

/// <summary>The client gave up on a silent stream: the CLI, not the node, ended the wait.</summary>
public sealed class ChatStreamWatchdogException : Exception
{
    public ChatStreamWatchdogException(bool noFirstFrame)
        : base(noFirstFrame
            ? "Connection watchdog: the response never started arriving."
            : "Connection watchdog: the response stopped arriving.")
    {
        NoFirstFrame = noFirstFrame;
    }

    /// <summary>True when nothing arrived at all; false when the stream stalled after its first frame.</summary>
    public bool NoFirstFrame { get; }
}
