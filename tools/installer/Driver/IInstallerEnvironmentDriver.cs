namespace XE_Local_AI_Engine.Installer.Driver;

using XE_Local_AI_Engine.Installer.Cli;

/// <summary>
///     The platform seam (plan §3 invariant 5, §7.2 — D7). Every OS-specific action the state machine
///     needs lives behind this interface so a future <c>LinuxInstallerDriver</c> is additive and the
///     orchestration (state machine, arg parsing, inventory, confirmation) stays platform-agnostic.
///     The RC1 implementation is <c>WindowsInstallerDriver</c>; unit tests substitute a mock so they
///     need no WSL/Docker/network.
/// </summary>
public interface IInstallerEnvironmentDriver
{
    /// <summary>Verify payload checksums, probe WSL2 capability/distro presence, read free disk (no mutation).</summary>
    Task<WslProbeResult> ProbeAsync(string bundlePath, CancellationToken cancellationToken = default);

    /// <summary>Verify the bundle's <c>SHA256SUMS</c> before any mutating action; throws on mismatch.</summary>
    Task VerifyPayloadChecksumAsync(string bundlePath, CancellationToken cancellationToken = default);

    /// <summary>Enable the WSL2 feature (<c>wsl --install --no-distribution</c>); may require a reboot.</summary>
    Task EnableWslAsync(CancellationToken cancellationToken = default);

    /// <summary>Import the bundled rootfs as the <c>xe-engine-runtime</c> distro.</summary>
    Task ImportDistroAsync(string bundlePath, CancellationToken cancellationToken = default);

    /// <summary>In-distro <c>docker load</c> from <c>/mnt/c</c> + retag + verify config Id (§6.3); returns the loaded image Id.</summary>
    Task<string> LoadImageAsync(string bundlePath, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Cheap "already-done" probe for an idempotent phase (plan §7.5 / code#5). Returns true when the
    ///     phase's effect already exists so the orchestrator can skip it on a mid-sequence re-run.
    ///     Probe failures are treated as "not satisfied" (run the phase) — never block install on a probe.
    /// </summary>
    Task<bool> IsPhaseSatisfiedAsync(InstallerPhaseProbe phase, string bundlePath, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The manifest-attributable artifacts shown in the pre-confirm teardown inventory (plan §3
    ///     invariant 1 / §7.4). Derived from the bundle manifest's declared container names via
    ///     <c>InstallerContainerOwnership.Owns</c>, plus the documented fixed paths. The vendored ps1
    ///     remains the actual deletion enforcer; this is the human-readable preview.
    /// </summary>
    Task<IReadOnlyList<string>> BuildTeardownInventoryAsync(string bundlePath, CancellationToken cancellationToken = default);

    /// <summary>Write the runtime manifest/runtime.json and create the DPAPI admin token.</summary>
    Task WriteConfigAsync(string bundlePath, CancellationToken cancellationToken = default);

    /// <summary>Shell out to <c>install-host-agent.ps1</c> (copy binaries + 4 shortcuts) and launch the Tray.</summary>
    Task InstallHostAgentAsync(string bundlePath, CancellationToken cancellationToken = default);

    /// <summary>In-distro <c>ollama pull</c> of the bootstrap model (online; D5); returns the pulled model id.</summary>
    Task<string> PullModelAsync(string bundlePath, CancellationToken cancellationToken = default);

    /// <summary>Post-install checks (web UI / admin status / port-conflict diagnostic, MED-7c).</summary>
    Task VerifyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Shell out to <c>uninstall-host-agent.ps1</c> (plan §7.4). <paramref name="dryRun" /> maps to
    ///     the ps1 <c>-WhatIf</c> (the ps1 has NO <c>-DryRun</c>); a non-dry-run is invoked with
    ///     <c>-Force</c> after the installer's own typed gate. Returns the completeness attestation.
    /// </summary>
    Task<TeardownResult> TeardownAsync(InstallerArguments arguments, bool dryRun, CancellationToken cancellationToken = default);
}
