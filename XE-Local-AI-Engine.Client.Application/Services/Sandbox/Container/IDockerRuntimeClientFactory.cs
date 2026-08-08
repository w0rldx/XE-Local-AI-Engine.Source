namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>
///     Creates an <see cref="IDockerRuntimeClient" /> for a resolved endpoint.
///     <para>
///         A factory rather than a registered singleton client because the endpoint is discovered, not configured:
///         the client cannot be built until resolution has run, and resolution can produce a different answer after a
///         restart. It is also the seam that lets a test substitute a client which reports settings that do not match
///         what was asked for, which is the only way the container hardening contract's fail-closed branch is reachable.
///     </para>
/// </summary>
public interface IDockerRuntimeClientFactory
{
    /// <summary>Create a client for <paramref name="endpoint" />. The caller owns disposal.</summary>
    IDockerRuntimeClient Create(DockerDaemonEndpoint endpoint);
}
