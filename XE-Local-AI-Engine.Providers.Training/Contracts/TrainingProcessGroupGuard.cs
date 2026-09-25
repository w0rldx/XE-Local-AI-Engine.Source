namespace XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>Decides whether a recorded trainer may be signalled through its process group rather than by pid alone.</summary>
public static class TrainingProcessGroupGuard
{
    /// <summary>
    ///     True only for a group the trainer itself leads (<c>setsid</c> makes pgid equal pid) that is neither init's, the
    ///     caller's (0) nor the host's own. Anything else would signal the node, its launcher or every process it shares.
    /// </summary>
    public static bool MaySignalGroup(int pid, int pgid, int hostProcessGroupId) =>
        pgid > 1 && pgid == pid && pgid != hostProcessGroupId;
}
