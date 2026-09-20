namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

using System.Diagnostics;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>
///     The production <see cref="ISandboxLauncher" />: applies the plan computed by <see cref="SandboxLaunchPlan" /> to a
///     <see cref="ProcessStartInfo" />.
/// </summary>
/// <remarks>
///     All decision logic lives in the plan, a pure function unit-tested without starting processes; this type is the thin adapter that
///     mutates the start info and layers in the wrapper's own environment.
/// </remarks>
public sealed class SandboxLauncher : ISandboxLauncher
{
    /// <summary>Grace added to the command's own timeout to form the scope's <c>RuntimeMaxSec</c>.</summary>
    /// <remarks>
    ///     The engine's timeout must be the control that normally fires; this one exists for when the engine is no longer there to fire
    ///     it, so it sits clearly behind rather than racing it.
    /// </remarks>
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

        // The wrapper's own environment (the user systemd bus address) is layered on AFTER the caller's scrubbed allow-list, systemd-run
        // needing it to reach the user manager. The innermost `env -u` layer removes it again before the executable is exec'd.
        foreach (var pair in descriptor.WrapperEnvironment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return descriptor;
    }

    /// <summary>Prepares and renders the isolated chain.</summary>
    /// <remarks>
    ///     Every failure path throws <see cref="SandboxIsolationUnavailableException" />, deliberately and unlike every other branch here:
    ///     a caller that asked for a filesystem boundary and silently got a command on the host filesystem is worse off than one that got
    ///     an error, because it goes on believing the boundary is there.
    /// </remarks>
    private static SandboxLaunchDescriptor CreateIsolatedDescriptor(ProcessStartInfo startInfo,
        SandboxLaunchPolicy policy,
        SandboxContainment containment,
        SandboxLaunchContext? context)
    {
        if (containment.FilesystemIsolation is not { } isolation)
        {
            throw new SandboxIsolationUnavailableException(containment.FilesystemIsolationUnavailableReason ?? "this host cannot run a command behind a filesystem boundary");
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
                WorkingDirectory = ResolveSandboxWorkingDirectory(startInfo.WorkingDirectory, jailRoot),
                AdditionalEnvironment = context.CommandEnvironment ?? new Dictionary<string, string>(StringComparer.Ordinal),
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

    /// <summary>
    ///     Translates the HOST working directory the provider resolved into the path the same directory has INSIDE the sandbox.
    /// </summary>
    /// <remarks>
    ///     The jail is <c>/work</c> there and never reachable at its host name, so a chain keeping the host path would chdir to a directory
    ///     that does not exist and the command would not start.
    /// </remarks>
    private static string ResolveSandboxWorkingDirectory(string? hostWorkingDirectory, string jailRoot)
    {
        if (string.IsNullOrEmpty(hostWorkingDirectory))
        {
            return SandboxIsolatedChain.WorkPath;
        }

        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hostWorkingDirectory));
        var canonicalJail = Path.TrimEndingDirectorySeparator(Path.GetFullPath(jailRoot));
        if (string.Equals(canonical, canonicalJail, StringComparison.Ordinal))
        {
            return SandboxIsolatedChain.WorkPath;
        }

        if (!canonical.StartsWith(canonicalJail + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            // The provider's own jail guard should already have made this impossible. Refusing rather than falling
            // back to /work keeps a bug there from turning into a command that quietly ran somewhere else.
            throw new SandboxIsolationUnavailableException($"the working directory '{hostWorkingDirectory}' is not inside the sandbox jail");
        }

        var relative = canonical[(canonicalJail.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');

        return $"{SandboxIsolatedChain.WorkPath}/{relative}";
    }
}
