namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Lifecycle of a playbook action. The full set is persisted now so later work needs no column change, but the
///     manual write path accepts only <see cref="Enabled" />/<see cref="Disabled" /> and injects only
///     <see cref="Enabled" /> actions. <see cref="Suggested" /> (analysis proposals) and <see cref="Archived" /> are
///     reserved for the self-improvement flow.
/// </summary>
public enum PlaybookActionState
{
    Suggested = 0,
    Enabled = 1,
    Disabled = 2,
    Archived = 3
}
