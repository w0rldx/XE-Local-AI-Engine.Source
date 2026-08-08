namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

using System.Diagnostics;

/// <summary>
///     The production <see cref="ISandboxLauncher" />: applies the plan computed by <see cref="SandboxLaunchPlan" /> to
///     a <see cref="ProcessStartInfo" />. All decision logic lives in the plan (a pure function, unit-tested without
///     starting processes); this type is the thin adapter that mutates the start info and layers in the wrapper's own
///     environment.
/// </summary>
public sealed class SandboxLauncher : ISandboxLauncher
{
    private readonly ISandboxContainmentProbe _probe;

    public SandboxLauncher(ISandboxContainmentProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <inheritdoc />
    public SandboxContainment Containment => _probe.Containment;

    /// <inheritdoc />
    public SandboxLaunchDescriptor Apply(ProcessStartInfo startInfo, SandboxLaunchPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(policy);

        var descriptor = SandboxLaunchPlan.Create(startInfo.FileName, [.. startInfo.ArgumentList], policy, _probe.Containment);

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
}
