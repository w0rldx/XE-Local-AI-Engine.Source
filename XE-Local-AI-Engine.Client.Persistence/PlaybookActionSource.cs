namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Provenance of a playbook action. P1 writes only <see cref="Manual" /> (human-authored);
///     <see cref="Analysis" /> (proposed by the deferred analysis agent) is reserved for later phases.
/// </summary>
public enum PlaybookActionSource
{
    Manual = 0,
    Analysis = 1
}
