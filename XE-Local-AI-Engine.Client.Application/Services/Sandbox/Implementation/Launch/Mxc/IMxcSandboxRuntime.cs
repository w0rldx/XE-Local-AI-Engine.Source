namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;

using Microsoft.Mxc.Sdk.V1;

/// <summary>
///     The seam over the MXC SDK's static entry points, so the probe and process adapter are unit-tested without the native library.
///     <see cref="MxcSandboxRuntime" /> is the only real implementation.
/// </summary>
public interface IMxcSandboxRuntime
{
    PlatformSupport GetPlatformSupport();

    ProbeOutput Probe(ContainerRequest? request);

    /// <summary>Starts the child; the caller owns the returned process and must wait on it (MXC enforces the timeout only inside a wait).</summary>
    Task<IMxcProcess> SpawnAsync(ContainerRequest request, CancellationToken cancellationToken);
}
