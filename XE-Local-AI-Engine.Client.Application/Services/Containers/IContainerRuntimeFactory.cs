namespace XE_Local_AI_Engine.Client.Services.Containers;

using XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>Creates an <see cref="IContainerRuntime" /> for an endpoint the resolver has already settled on.</summary>
/// <remarks>
///     Derived from <see cref="IDockerRuntimeClientFactory" /> so the shared daemon probe and this layer speak one
///     abstraction, and adds the endpoint-taking creator: by the time the resolver wants a client it has already
///     resolved an endpoint and must not re-resolve through Development Mode's options. The inherited
///     <see cref="IDockerRuntimeClientFactory.Create" /> keeps Dev Mode's shape and is never called from here.
/// </remarks>
public interface IContainerRuntimeFactory : IDockerRuntimeClientFactory
{
    /// <summary>
    ///     Create a runtime client for <paramref name="endpoint" /> using the container-runtime timeouts, which are
    ///     sized for a half-hour image pull and a thirty-second graceful stop rather than for a ten-second preflight.
    ///     The caller owns disposal.
    /// </summary>
    IContainerRuntime CreateRuntime(DockerDaemonEndpoint endpoint);
}
