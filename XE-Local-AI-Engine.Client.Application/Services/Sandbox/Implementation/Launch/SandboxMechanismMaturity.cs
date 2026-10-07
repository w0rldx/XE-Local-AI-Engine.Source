namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

/// <summary>How far a sandbox launch MECHANISM is trusted, as distinct from which provider uses it.</summary>
/// <remarks>
///     Maturity belongs to a mechanism, not a provider: the <c>process</c> provider launches through <c>setsid</c>, <c>systemd-run</c>,
///     <c>unshare</c> and <c>bwrap</c> (all <see cref="Stable" />) and, on Windows, through the MXC AppContainer boundary
///     (<see cref="Preview" />). A <see cref="Preview" /> mechanism serves a role only while the operator's
///     <c>ExecutionPreviewsEnabled</c> node setting is on, and the capability surface never reports it as <c>Isolated</c>. ADR 0019.
/// </remarks>
public enum SandboxMechanismMaturity
{
    /// <summary>Shipped, live-verified on its platform, eligible without an opt-in.</summary>
    Stable = 0,

    /// <summary>Eligible only while execution previews are enabled; reported as <c>PreviewIsolated</c>, never <c>Isolated</c>.</summary>
    Preview = 1
}
