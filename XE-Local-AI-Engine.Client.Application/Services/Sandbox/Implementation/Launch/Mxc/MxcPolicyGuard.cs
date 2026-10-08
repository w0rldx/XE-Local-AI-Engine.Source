namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;

using Microsoft.Mxc.Sdk.V1;

/// <summary>
///     Fails closed on any MXC request property that weakens enforcement. Runs on every request before it reaches the SDK.
/// </summary>
/// <remarks>
///     The list is the SDK 1.0.0 surface (pinned exactly in <c>Directory.Packages.props</c>): learning mode, denial capture,
///     capability grants, PSEC-only enumerate grants and proxy peers, any network allowance or proxy, any UI or clipboard allowance,
///     a policy kept after exit, and an environment inherited from the user's profile. Unset (null) network and UI sections are
///     rejected too: their backend default is not a documented deny. MXC has no property named "permissive" or "audit".
/// </remarks>
public static class MxcPolicyGuard
{
    public static void Assert(ContainerRequest request)
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

        if (request.Filesystem is { ClearPolicyOnExit: false })
        {
            Reject("Filesystem.ClearPolicyOnExit is false (grants would outlive the run)");
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

        if (request.Ui is not { Disable: true, Clipboard: ClipboardPolicy.None, AllowInputInjection: false })
        {
            Reject("Ui is missing or allows UI, clipboard or input injection");
        }

        if (request.Environment is null || request.InheritDefaultEnvironment)
        {
            Reject("Environment is null or inherits the default (user profile) environment block");
        }
    }

    private static void Reject(string reason) =>
        throw new SandboxCapabilityNotSupportedException($"The MXC policy was refused because it weakens containment: {reason}.");
}
