namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;

using Microsoft.Mxc.Sdk.V1;

/// <summary>
///     Maps an <see cref="MxcLaunchRequest" /> onto the one MXC policy the engine runs: Windows ProcessContainer, enforcing, no network,
///     no UI, a single writable jail, engine-granted read-only trees, explicit deny roots and a clean environment.
/// </summary>
/// <remarks>
///     Pure object construction; nothing here calls the native library, so it is platform-neutral and unit-tested on Linux. Every
///     request it builds passes <see cref="MxcPolicyGuard.Assert" />, which <see cref="MxcSandboxRuntime" /> runs again before spawning.
/// </remarks>
public static class MxcPolicyMapper
{
    /// <summary>The command the probe validates; it is never run.</summary>
    internal const string ProbeCommand = "cmd.exe /c exit 0";

    public static ContainerRequest Build(MxcLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.JailRoot) || string.IsNullOrEmpty(request.WorkingDirectory))
        {
            throw new ArgumentException("An MXC launch needs a jail root and a working directory.", nameof(request));
        }

        var environment = new Dictionary<string, string>(request.Environment.Count, StringComparer.Ordinal);
        foreach (var (key, value) in request.Environment)
        {
            // The SDK's own rule; rejected here so the failure names the variable instead of surfacing as a native error.
            if (key.Length == 0 || key.Contains('=', StringComparison.Ordinal))
            {
                throw new ArgumentException($"Environment variable name '{key}' is empty or contains '='.", nameof(request));
            }

            environment[key] = value;
        }

        var containerRequest = CreateLockedRequest(WindowsCommandLine.Join(request.Executable, request.Arguments));
        containerRequest.Filesystem = new FilesystemPolicy
        {
            ReadwritePaths = [request.JailRoot],
            ReadonlyPaths = [.. request.ReadOnlyTrees],
            DeniedPaths = [.. request.DeniedRoots],
            ClearPolicyOnExit = true,
        };
        containerRequest.Environment = environment;
        containerRequest.InheritDefaultEnvironment = false;
        containerRequest.WorkingDirectory = request.WorkingDirectory;
        containerRequest.TimeoutMs = ToTimeoutMs(request.Timeout);
        return containerRequest;
    }

    /// <summary>
    ///     The request the capability probe asks MXC about: the same containment, network and UI policy every launch uses, so the tier
    ///     and warnings it reports are the ones a real launch gets. No filesystem grants, so probing touches no ACL.
    /// </summary>
    public static ContainerRequest BuildProbeRequest()
    {
        var request = CreateLockedRequest(ProbeCommand);
        request.Environment = new Dictionary<string, string>(StringComparer.Ordinal);
        request.InheritDefaultEnvironment = false;
        return request;
    }

    private static ContainerRequest CreateLockedRequest(string command) =>
        new(command)
        {
            Containment = new Containment.ProcessContainer
            {
                LearningMode = false,
                Capabilities = [],
                CaptureDenials = null,
                Ui = new ProcessContainerUiPolicy
                {
                    Isolation = ProcessContainerUiIsolation.Container,
                    DesktopSystemControl = false,
                    SystemSettings = ProcessContainerSystemSettings.None,
                    Ime = false,
                },
                Filesystem = null,
                Network = null,
            },
            Network = new NetworkPolicy
            {
                Egress = new NetworkEgressPolicy
                {
                    Default = NetworkAction.Deny
                },
                Ingress = new NetworkIngressPolicy
                {
                    Default = NetworkAction.Deny,
                    HostLoopback = NetworkAction.Deny
                },
            },
            Ui = new UiPolicy
            {
                Disable = true,
                Clipboard = ClipboardPolicy.None,
                AllowInputInjection = false
            },
        };

    /// <summary>MXC reads 0 as "no timeout", so a sub-millisecond timeout rounds up to 1 ms rather than silently removing the bound.</summary>
    private static uint? ToTimeoutMs(TimeSpan? timeout)
    {
        if (timeout is not { } value)
        {
            return null;
        }

        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), value, "An MXC timeout must be positive; null means none.");
        }

        var milliseconds = Math.Ceiling(value.TotalMilliseconds);
        return milliseconds >= uint.MaxValue ? uint.MaxValue : Math.Max(1u, (uint)milliseconds);
    }
}
