namespace XE_Local_AI_Engine.Installer.Driver;

/// <summary>
///     The install phases that carry a cheap "already-done ⇒ skip" idempotency probe (plan §7.5 table).
///     The orchestrator asks <see cref="IInstallerEnvironmentDriver.IsPhaseSatisfiedAsync" /> before
///     executing the phase action; a satisfied phase is a no-op on re-run. Phases not listed here have
///     no cheap cross-platform probe and always execute (their own scripts are internally idempotent —
///     <c>docker load</c>, <c>install-host-agent.ps1</c>, and the model-pull wait are all re-runnable).
/// </summary>
public enum InstallerPhaseProbe
{
    /// <summary>Skip the distro import when <c>xe-engine-runtime</c> is already registered.</summary>
    DistroImport,

    /// <summary>Skip the image load when the expected config Id is already present in the distro daemon.</summary>
    ImageLoad
}
