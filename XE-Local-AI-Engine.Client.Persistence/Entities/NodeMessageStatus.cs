namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal static class NodeMessageStatus
{
    public const string Pending = "pending";
    public const string Queued = "queued";
    public const string Streaming = "streaming";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
    public const string Interrupted = "interrupted";

    public static readonly IReadOnlySet<string> NonTerminal = new HashSet<string>(StringComparer.Ordinal)
    {
        Pending,
        Queued,
        Streaming
    };
}
