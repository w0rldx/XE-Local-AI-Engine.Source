namespace XE_Local_AI_Engine.Installer.StateMachine;

/// <summary>
///     The ordered install state-machine phases (plan §7.5). Each phase is idempotent and
///     resumable: re-running a completed phase is a no-op, and the cursor persists across the
///     one-time WSL-enable reboot so a relaunch resumes from where it stopped.
/// </summary>
public enum InstallPhase
{
    Probe,
    WslEnable,
    DistroImport,
    ImageLoad,
    ConfigWrite,
    HostAgentInstall,
    ModelPull,
    Verify,
    Finalize,
    Completed
}
