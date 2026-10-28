namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal static class NodeMessageFeedbackRating
{
    public const string Up = "up";
    public const string Down = "down";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Up,
        Down
    };
}
