namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Provenance of a playbook action. The current write path writes only <see cref="Manual" /> (human-authored);
///     <see cref="Analysis" /> (proposed by the analysis agent) and <see cref="Extracted" /> (mined post-run from a
///     completed conversation by the adaptive-memory extraction service) are reserved for the self-improvement flow. The
///     value is persisted as a plain int, so appending a new member is additive — no column change and no encode/decode
///     edit.
/// </summary>
public enum PlaybookActionSource
{
    Manual = 0,
    Analysis = 1,

    /// <summary>Mined post-run from a completed conversation by the adaptive-memory extraction service.</summary>
    Extracted = 2
}
