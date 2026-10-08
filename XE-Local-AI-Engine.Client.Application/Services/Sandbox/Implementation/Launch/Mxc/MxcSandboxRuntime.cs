namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;

using System.Runtime.Versioning;
using Microsoft.Mxc.Sdk.V1;

/// <summary>The real <see cref="IMxcSandboxRuntime" />: the SDK statics, behind <see cref="MxcPolicyGuard" /> on every spawn.</summary>
/// <remarks>
///     Windows only: the package carries no platform attribute of its own, so this type-level annotation is what makes CA1416 hold
///     every caller to an <c>OperatingSystem.IsWindows()</c> branch. Telemetry stays off (the SDK default with no options).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MxcSandboxRuntime : IMxcSandboxRuntime
{
    public PlatformSupport GetPlatformSupport() =>
        MxcPlatform.GetPlatformSupport();

    public ProbeOutput Probe(ContainerRequest? request) =>
        MxcContainer.Probe(request);

    public async Task<IMxcProcess> SpawnAsync(ContainerRequest request, CancellationToken cancellationToken)
    {
        MxcPolicyGuard.Assert(request);
        return await MxcContainer.SpawnAsync(request, options: null, cancellationToken);
    }
}
