namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

using System.Diagnostics;
using System.Globalization;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>
///     The production <see cref="ISandboxLauncher" />: applies the plan computed by <see cref="SandboxLaunchPlan" /> to
///     a <see cref="ProcessStartInfo" />. All decision logic lives in the plan (a pure function, unit-tested without
///     starting processes); this type is the thin adapter that mutates the start info and layers in the wrapper's own
///     environment.
/// </summary>
public sealed class SandboxLauncher : ISandboxLauncher
{
    /// <summary>
    ///     Grace added to the command's own timeout to form the scope's <c>RuntimeMaxSec</c>. The engine's timeout
    ///     must be the control that normally fires; this one exists for the case where the engine is no longer there
    ///     to fire it, so it has to sit clearly behind it rather than racing it.
    /// </summary>
    private static readonly TimeSpan ScopeLifetimeGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     The scope ceiling for a command that names no timeout of its own. An isolated command is expected to carry
    ///     one; this bounds the case where it does not, so a jail cannot outlive the engine indefinitely.
    /// </summary>
    private static readonly TimeSpan DefaultScopeLifetime = TimeSpan.FromHours(1);

    private readonly ISandboxContainmentProbe _probe;

    public SandboxLauncher(ISandboxContainmentProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <inheritdoc />
    public SandboxContainment Containment => _probe.Containment;

    /// <inheritdoc />
    public SandboxLaunchDescriptor Apply(ProcessStartInfo startInfo, SandboxLaunchPolicy policy, SandboxLaunchContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(policy);

        var containment = _probe.Containment;
        var descriptor = policy.Isolation == SandboxIsolationMode.Filesystem
            ? CreateIsolatedDescriptor(startInfo, policy, containment, context)
            : SandboxLaunchPlan.Create(startInfo.FileName, [.. startInfo.ArgumentList], policy, containment);

        startInfo.FileName = descriptor.FileName;
        startInfo.ArgumentList.Clear();
        foreach (var argument in descriptor.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The wrapper's own environment (the user systemd bus address) is layered on AFTER the caller's scrubbed
        // allow-list, because systemd-run cannot reach the user manager without it. The innermost `env -u` layer that
        // SandboxLaunchPlan emits removes it again before the sandboxed executable is exec'd, so the child's observable
        // environment is unchanged by this addition.
        foreach (var pair in descriptor.WrapperEnvironment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return descriptor;
    }

    /// <summary>
    ///     Prepares and renders the isolated chain. Every failure path throws
    ///     <see cref="SandboxIsolationUnavailableException" /> — deliberately, and unlike every other branch in this
    ///     type. A caller that asked for a filesystem boundary and silently got a command running on the host
    ///     filesystem would be worse off than one that got an error, because it would go on believing the boundary was
    ///     there.
    /// </summary>
    private static SandboxLaunchDescriptor CreateIsolatedDescriptor(ProcessStartInfo startInfo,
        SandboxLaunchPolicy policy,
        SandboxContainment containment,
        SandboxLaunchContext? context)
    {
        if (containment.FilesystemIsolation is not { } isolation)
        {
            throw new SandboxIsolationUnavailableException(
                containment.FilesystemIsolationUnavailableReason ?? "this host cannot run a command behind a filesystem boundary");
        }

        if (context?.JailRoot is not { } jailRoot || string.IsNullOrWhiteSpace(jailRoot))
        {
            throw new SandboxIsolationUnavailableException("an isolated launch needs the sandbox jail directory, and none was supplied");
        }

        var lifetime = context.CommandTimeout is { } timeout && timeout > TimeSpan.Zero
            ? timeout + ScopeLifetimeGrace
            : DefaultScopeLifetime;

        var launch = SandboxIsolationLaunch.Create(isolation,
            new SandboxIsolationLaunchRequest
            {
                JailRoot = jailRoot,
                Executable = startInfo.FileName,
                Arguments = [.. startInfo.ArgumentList],
                ReadOnlyTrees = policy.ReadOnlyTrees,
                ResourceLimits = policy.ResourceLimits,
                ThreadLimit = policy.ThreadLimit,
                RuntimeMaxSeconds = (long)Math.Ceiling(lifetime.TotalSeconds),
                Role = policy.Role
            });

        try
        {
            return SandboxLaunchPlan.CreateIsolated(launch, policy, containment);
        }
        catch
        {
            launch.Dispose();
            throw;
        }
    }
}
