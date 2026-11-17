namespace XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>What one read of <c>/proc/[pid]/stat</c> proves about a recorded process.</summary>
/// <remarks>
///     Only <see cref="Gone" /> lets a caller drop the last record of a process. <see cref="Unknown" /> (the entry exists but
///     could not be read or parsed) never counts as gone, because treating it so would strand a live GPU holder. It is the
///     default so an unset value fails safe.
/// </remarks>
public enum TrainingProcessPresence
{
    /// <summary>The stat entry exists but could not be read or parsed.</summary>
    Unknown,

    /// <summary>The recorded process is still running.</summary>
    Alive,

    /// <summary>No such process, a zombie, or a different process now holding the pid.</summary>
    Gone
}
