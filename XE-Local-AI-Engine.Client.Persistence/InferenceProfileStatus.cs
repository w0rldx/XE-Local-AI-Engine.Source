namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Lifecycle state of a persisted inference profile.
/// </summary>
/// <remarks>
///     <see cref="Explored" /> rows carry the drafted launch args llama.cpp auto-fit produced, so a benchmark and a
///     later freeze replay the SAME config; <see cref="Frozen" /> rows are benchmark-justified and replayed verbatim
///     on spawn; <see cref="Stale" /> rows had an invalidation trigger fire (build change, hardware/driver delta, or
///     live free-VRAM below the frozen baseline). The numeric values are persisted and must never be renumbered.
/// </remarks>
public enum InferenceProfileStatus
{
    /// <summary>Auto-fit drafted the launch args; not yet benchmark-justified.</summary>
    Explored = 0,

    /// <summary>A successful benchmark justified the args; replayed verbatim on spawn.</summary>
    Frozen = 1,

    /// <summary>An invalidation trigger fired; re-exploration is the only path back to auto-fit.</summary>
    Stale = 2
}
