namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Lifecycle of a playbook action. The full set is persisted now so later phases need no column change, but P1
///     accepts only <see cref="Enabled" />/<see cref="Disabled" /> on the write path and injects only
///     <see cref="Enabled" /> actions. <see cref="Suggested" /> (analysis proposals) and <see cref="Archived" /> are
///     reserved for the deferred self-improvement phases.
/// </summary>
public enum PlaybookActionState
{
    Suggested = 0,
    Enabled = 1,
    Disabled = 2,
    Archived = 3
}
