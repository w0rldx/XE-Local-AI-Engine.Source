namespace XE_Local_AI_Engine.Client.Services.Containers;

using XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>
///     Creates an <see cref="IContainerRuntime" /> for an endpoint the resolver has already settled on.
///     <para>
///         Derived from <see cref="IDockerRuntimeClientFactory" /> so that the shared daemon probe and this layer
///         speak one abstraction, and adds the endpoint-taking creator this layer needs: by the time the resolver
///         wants a client it has already resolved an endpoint, and it must not re-resolve through Development Mode's
///         options. The inherited <see cref="IDockerRuntimeClientFactory.Create" /> keeps Development Mode's shape and
///         is never called from this layer.
///     </para>
/// </summary>
public interface IContainerRuntimeFactory : IDockerRuntimeClientFactory
{
    /// <summary>
    ///     Create a runtime client for <paramref name="endpoint" /> using the container-runtime timeouts, which are
    ///     sized for a half-hour image pull and a thirty-second graceful stop rather than for a ten-second preflight.
    ///     The caller owns disposal.
    /// </summary>
    IContainerRuntime CreateRuntime(DockerDaemonEndpoint endpoint);
}
