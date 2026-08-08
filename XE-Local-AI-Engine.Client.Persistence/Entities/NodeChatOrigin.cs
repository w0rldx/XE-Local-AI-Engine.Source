namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal static class NodeChatOrigin
{
    public const string Local = "Local";
    public const string Remote = "Remote";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Local,
        Remote
    };
}
