namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;

using Microsoft.Mxc.Sdk.V1;

/// <summary>
///     Fails closed on any MXC request property that weakens enforcement. Runs on every request before it reaches the SDK.
/// </summary>
/// <remarks>
///     The list is the SDK 1.0.0 surface (pinned exactly in <c>Directory.Packages.props</c>): learning mode, denial capture,
///     capability grants, PSEC-only enumerate grants and proxy peers, any network allowance or proxy, clipboard access, input
///     injection or non-container UI isolation (Win32k itself is allowed, ADR 0019 amendment), a launch policy that is not exactly
///     one jail grant cleared on exit, and an environment inherited from the user's profile. Unset (null) network and UI sections are
///     rejected too: their backend default is not a documented deny. MXC has no property named "permissive" or "audit".
/// </remarks>
public static class MxcPolicyGuard
{
    /// <summary>Checks a LAUNCH request: on top of the shared rules, exactly one read-write grant (the jail) cleared on exit.</summary>
    public static void Assert(ContainerRequest request)
    {
        AssertShared(request);

        if (request.Filesystem is not { ReadwritePaths.Count: 1, ClearPolicyOnExit: true })
        {
            Reject("Filesystem is missing, grants other than exactly one read-write path (the jail), or does not set ClearPolicyOnExit");
        }
    }

    /// <summary>Checks the capability PROBE request, which carries no filesystem grants at all.</summary>
    public static void AssertProbe(ContainerRequest request)
    {
        AssertShared(request);

        if (request.Filesystem is not null)
        {
            Reject("the probe request carries a Filesystem policy");
        }
    }

    private static void AssertShared(ContainerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Containment is not Containment.ProcessContainer container)
        {
            Reject($"containment '{request.Containment?.GetType().Name ?? "null"}' is not ProcessContainer");
            return;
        }

        if (container.LearningMode)
        {
            Reject("ProcessContainer.LearningMode is set (deny-and-record learning mode)");
        }

        if (container.Capabilities is { Count: > 0 })
        {
            Reject("ProcessContainer.Capabilities grants AppContainer capabilities");
        }

        if (container.CaptureDenials is not null)
        {
            Reject("ProcessContainer.CaptureDenials is set (denial capture; Allow mode relaxes containment)");
        }

        if (container.Ui is not { Isolation: ProcessContainerUiIsolation.Container, DesktopSystemControl: false, Ime: false, SystemSettings: ProcessContainerSystemSettings.None })
        {
            Reject("ProcessContainer.Ui is missing or allows desktop, IME, system-settings or non-container UI access");
        }

        if (container.Filesystem is not null)
        {
            Reject("ProcessContainer.Filesystem (EnumeratePaths) is set");
        }

        if (container.Network is not null)
        {
            Reject("ProcessContainer.Network (AllowedProxyPeer) is set");
        }


        if (request.Network is not { Egress: { Default: NetworkAction.Deny }, Ingress: { Default: NetworkAction.Deny, HostLoopback: NetworkAction.Deny }, })
        {
            Reject("Network is missing or does not deny egress, ingress and host loopback");
        }

        if (request.Network?.Egress?.Allow is { Count: > 0 })
        {
            Reject("Network.Egress.Allow carries allow rules");
        }

        if (request.Network?.RuntimeConfig?.NetworkProxy is not null)
        {
            Reject("Network.RuntimeConfig.NetworkProxy is set");
        }

        // Disable may be either value since the ADR 0019 amendment: Win32k is allowed, clipboard and input injection are not.
        if (request.Ui is not { Clipboard: ClipboardPolicy.None, AllowInputInjection: false })
        {
            Reject("Ui is missing or allows clipboard access or input injection");
        }

        if (request.Environment is null || request.InheritDefaultEnvironment)
        {
            Reject("Environment is null or inherits the default (user profile) environment block");
        }
    }

    private static void Reject(string reason) =>
        throw new SandboxCapabilityNotSupportedException($"The MXC policy was refused because it weakens containment: {reason}.");
}
