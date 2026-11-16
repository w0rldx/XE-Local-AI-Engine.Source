namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

using System.Diagnostics;

/// <summary>
///     The single injection point through which every sandboxed child is launched: it REWRITES an already-composed
///     <see cref="ProcessStartInfo" /> so the command runs under the strongest containment the host supports.
/// </summary>
/// <remarks>
///     Everything else about the launch is untouched — the jail, the scrubbed environment allow-list, stream redirection, the per-command
///     timeout and tree-kill all behave as before, because only <see cref="ProcessStartInfo.FileName" /> and
///     <see cref="ProcessStartInfo.ArgumentList" /> change. Wrapping rather than replacing is deliberate: the provider keeps ownership of
///     the <see cref="Process" />, so no existing guard is re-plumbed and none can regress.
/// </remarks>
public interface ISandboxLauncher
{
    /// <summary>The host containment this launcher can apply, for capability advertisement.</summary>
    SandboxContainment Containment { get; }

    /// <summary>
    ///     Rewrites <paramref name="startInfo" /> in place to wrap its command under the mechanisms both requested by
    ///     <paramref name="policy" /> and available on this host, and returns what was actually applied.
    /// </summary>
    /// <remarks>
    ///     An unavailable egress or process-group layer degrades and is reported. Two things never degrade: a
    ///     <see cref="SandboxIsolationMode.Filesystem" /> boundary that cannot be prepared throws, and so do ceilings no mechanism applies
    ///     (ADR 0025), because a policy carrying either was accepted against a host measured able to deliver it.
    ///     <paramref name="context" /> supplies the per-command facts the boundary needs.
    /// </remarks>
    SandboxLaunchDescriptor Apply(ProcessStartInfo startInfo, SandboxLaunchPolicy policy, SandboxLaunchContext? context = null);
}
