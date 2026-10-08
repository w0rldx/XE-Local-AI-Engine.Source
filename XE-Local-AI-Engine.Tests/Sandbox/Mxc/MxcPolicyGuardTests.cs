namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using Microsoft.Mxc.Sdk.V1;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="MxcPolicyGuard" /> refuses every MXC 1.0.0 property that weakens enforcement. Each case starts from the mapper's
///     request (proven to pass) and changes exactly one property, so a red case names the property the guard stopped catching.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class MxcPolicyGuardTests
{
    [Test]
    [Arguments("NotProcessContainer")]
    [Arguments("LearningMode")]
    [Arguments("Capabilities")]
    [Arguments("CaptureDenials")]
    [Arguments("CaptureDenialsAllow")]
    [Arguments("ContainerUiMissing")]
    [Arguments("ContainerUiIsolation")]
    [Arguments("ContainerUiDesktopSystemControl")]
    [Arguments("ContainerUiIme")]
    [Arguments("ContainerUiSystemSettings")]
    [Arguments("EnumeratePaths")]
    [Arguments("AllowedProxyPeer")]
    [Arguments("ClearPolicyOnExitFalse")]
    [Arguments("NetworkMissing")]
    [Arguments("EgressDefaultAllow")]
    [Arguments("EgressDefaultUnset")]
    [Arguments("EgressAllowRule")]
    [Arguments("IngressDefaultAllow")]
    [Arguments("HostLoopbackAllow")]
    [Arguments("NetworkProxy")]
    [Arguments("UiMissing")]
    [Arguments("UiEnabled")]
    [Arguments("Clipboard")]
    [Arguments("InputInjection")]
    [Arguments("EnvironmentNull")]
    [Arguments("InheritDefaultEnvironment")]
    public async Task Assert_RejectsEachWeakeningProperty(string property)
    {
        var request = SafeRequest();
        Weaken(request, property);

        AssertEx.Throws<SandboxCapabilityNotSupportedException>(() => MxcPolicyGuard.Assert(request), property);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Assert_AcceptsTheMappersRequest_WithClearPolicyOnExitLeftAtItsTrueDefault()
    {
        var request = SafeRequest();
        request.Filesystem!.ClearPolicyOnExit = null;

        AssertEx.DoesNotThrow(() => MxcPolicyGuard.Assert(request), "an unset ClearPolicyOnExit defaults to true in MXC");
        await Task.CompletedTask;
    }

    private static ContainerRequest SafeRequest() =>
        MxcPolicyMapper.Build(new MxcLaunchRequest
        {
            Executable = "cmd.exe",
            Arguments = ["/c", "echo"],
            WorkingDirectory = @"C:\jail",
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
            JailRoot = @"C:\jail",
            ReadOnlyTrees = [],
            DeniedRoots = [],
            Timeout = TimeSpan.FromSeconds(5),
        });

    private static void Weaken(ContainerRequest request, string property)
    {
        var container = (Containment.ProcessContainer)request.Containment;
        switch (property)
        {
            case "NotProcessContainer":
                request.Containment = new Containment.Process();
                break;
            case "LearningMode":
                container.LearningMode = true;
                break;
            case "Capabilities":
                container.Capabilities.Add("internetClient");
                break;
            case "CaptureDenials":
                container.CaptureDenials = new CaptureDenialsPolicy();
                break;
            case "CaptureDenialsAllow":
                container.CaptureDenials = new CaptureDenialsPolicy
                {
                    Mode = CaptureDenialsMode.Allow
                };
                break;
            case "ContainerUiMissing":
                container.Ui = null;
                break;
            case "ContainerUiIsolation":
                container.Ui!.Isolation = ProcessContainerUiIsolation.Desktop;
                break;
            case "ContainerUiDesktopSystemControl":
                container.Ui!.DesktopSystemControl = true;
                break;
            case "ContainerUiIme":
                container.Ui!.Ime = true;
                break;
            case "ContainerUiSystemSettings":
                container.Ui!.SystemSettings = ProcessContainerSystemSettings.All;
                break;
            case "EnumeratePaths":
                container.Filesystem = new ProcessContainerFilesystemPolicy
                {
                    EnumeratePaths = [@"C:\"]
                };
                break;
            case "AllowedProxyPeer":
                container.Network = new ProcessContainerNetworkPolicy
                {
                    AllowedProxyPeer = "127.0.0.1:8080"
                };
                break;
            case "ClearPolicyOnExitFalse":
                request.Filesystem!.ClearPolicyOnExit = false;
                break;
            case "NetworkMissing":
                request.Network = null;
                break;
            case "EgressDefaultAllow":
                request.Network!.Egress!.Default = NetworkAction.Allow;
                break;
            case "EgressDefaultUnset":
                request.Network!.Egress!.Default = null;
                break;
            case "EgressAllowRule":
                request.Network!.Egress!.Allow =
                [
                    new NetworkRulePolicy
                    {
                        To = [new NetworkPeerPolicy("0.0.0.0/0")]
                    }
                ];
                break;
            case "IngressDefaultAllow":
                request.Network!.Ingress!.Default = NetworkAction.Allow;
                break;
            case "HostLoopbackAllow":
                request.Network!.Ingress!.HostLoopback = NetworkAction.Allow;
                break;
            case "NetworkProxy":
                request.Network!.RuntimeConfig = new NetworkRuntimeConfig
                {
                    NetworkProxy = "http://127.0.0.1:3128"
                };
                break;
            case "UiMissing":
                request.Ui = null;
                break;
            case "UiEnabled":
                request.Ui!.Disable = false;
                break;
            case "Clipboard":
                request.Ui!.Clipboard = ClipboardPolicy.Read;
                break;
            case "InputInjection":
                request.Ui!.AllowInputInjection = true;
                break;
            case "EnvironmentNull":
                request.Environment = null;
                break;
            case "InheritDefaultEnvironment":
                request.InheritDefaultEnvironment = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(property), property, "unknown weakening case");
        }
    }
}
