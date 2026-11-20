namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>One read of <c>/proc/[pid]/stat</c>: what it proves about the pid, and the parsed fields when it parsed.</summary>
/// <remarks>A zombie is <see cref="TrainingProcessPresence.Gone" /> yet keeps its <see cref="Stat" />: its group may still hold live workers.</remarks>
internal sealed class TrainingProcessStatRead
{
    public static readonly TrainingProcessStatRead Missing = new()
    {
        Presence = TrainingProcessPresence.Gone
    };

    public static readonly TrainingProcessStatRead Unreadable = new()
    {
        Presence = TrainingProcessPresence.Unknown
    };

    public required TrainingProcessPresence Presence { get; init; }

    public TrainingProcessStat? Stat { get; init; }
}
