namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Provenance of a golden conversation case. <see cref="Manual" /> cases are hand-authored by the operator;
///     <see cref="Harvested" /> cases are proposed from a thumbs-up assistant turn and staged inert until approved.
/// </summary>
public enum GoldenConversationSource
{
    Manual = 0,
    Harvested = 1
}
