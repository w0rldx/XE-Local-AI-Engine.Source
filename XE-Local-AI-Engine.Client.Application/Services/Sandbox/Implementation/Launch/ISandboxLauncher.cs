namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

using System.Diagnostics;

/// <summary>
///     The single injection point through which every sandboxed child is launched. It REWRITES an already-composed
///     <see cref="ProcessStartInfo" /> so the command runs under the strongest containment the host supports, and leaves
///     everything else about the launch untouched — the working-directory jail, the scrubbed environment allow-list,
///     stream redirection, the per-command timeout and tree-kill all continue to work exactly as before, because the
///     launcher only changes <see cref="ProcessStartInfo.FileName" /> and
///     <see cref="ProcessStartInfo.ArgumentList" />.
///     <para>
///         Wrapping rather than replacing is deliberate: the provider keeps ownership of the <see cref="Process" />, so
///         no existing guard (path traversal, <c>O_NOFOLLOW</c>, <c>Process.Kill(entireProcessTree)</c>) is re-plumbed
///         and none can regress.
///     </para>
/// </summary>
public interface ISandboxLauncher
{
    /// <summary>The host containment this launcher can apply, for capability advertisement.</summary>
    SandboxContainment Containment { get; }

    /// <summary>
    ///     Rewrites <paramref name="startInfo" /> in place to wrap its command under the mechanisms that are both
    ///     requested by <paramref name="policy" /> and available on this host, and returns what was actually applied.
    ///     Must never throw for an unavailable mechanism — it degrades, reports, and lets the command run.
    /// </summary>
    SandboxLaunchDescriptor Apply(ProcessStartInfo startInfo, SandboxLaunchPolicy policy);
}
