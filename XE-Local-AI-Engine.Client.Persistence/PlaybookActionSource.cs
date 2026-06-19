namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Provenance of a playbook action. The current write path writes only <see cref="Manual" /> (human-authored);
///     <see cref="Analysis" /> (proposed by the analysis agent) is reserved for the self-improvement flow.
/// </summary>
public enum PlaybookActionSource
{
    Manual = 0,
    Analysis = 1
}
