namespace XE_Local_AI_Engine.Installer.State;

using XE_Local_AI_Engine.Installer.StateMachine;

/// <summary>
///     The resumable state-machine cursor (plan §6.1, <c>install-state.json</c>). Records the phase
///     reached, the reboot-pending flag (mirrors the prereq note's <c>wsl-install-reboot-required</c>),
///     and the last error. Cleared on a successful finalize — completed state then lives only in
///     <see cref="InstallManifest" />.
/// </summary>
public sealed record InstallState
{
    public required InstallPhase Phase { get; init; }

    public bool RebootPending { get; init; }

    public string? LastError { get; init; }
}
