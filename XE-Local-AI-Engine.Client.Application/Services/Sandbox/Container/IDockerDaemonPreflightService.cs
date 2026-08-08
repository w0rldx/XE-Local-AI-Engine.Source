namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>
///     Answers "can Development Mode create a container right now, and if not, what should the operator do about it".
/// </summary>
public interface IDockerDaemonPreflightService
{
    /// <summary>
    ///     Resolve the endpoint, probe the daemon, and compare what answered against this node's pinned attestation.
    ///     Pins on first use. Never throws for an unavailable daemon — an unavailable daemon is a result.
    /// </summary>
    Task<DockerDaemonPreflight> InspectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Record the operator's explicit approval of the daemon currently reachable, and return the resulting
    ///     preflight.
    ///     <para>
    ///         <paramref name="expectedDaemonId" /> is the daemon the operator was shown. It is required, and the
    ///         confirmation is refused when it does not match what is reachable now: without it, a confirmation
    ///         issued against one daemon could land on whichever daemon happened to answer by the time the request
    ///         arrived — which would make the control approve something nobody looked at.
    ///     </para>
    /// </summary>
    Task<DockerDaemonPreflight> ConfirmAsync(string expectedDaemonId, CancellationToken cancellationToken = default);
}
