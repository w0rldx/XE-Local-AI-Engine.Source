namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>Persistence for the pinned daemon attestation.</summary>
/// <remarks>
///     Host-machine state, not user data, which is why it is a file under the node data directory rather than a row in the node database:
///     the preflight must answer "which daemon is this install approved against" without reference to any project, user or tenant; the
///     approval is a property of the machine, so it must not travel with a restored database; and a table would need a migration, a
///     working <c>Down</c>, a regenerated model snapshot and an allow-list entry, none of which buys anything for a single record with no
///     relations.
/// </remarks>
public interface IDockerDaemonAttestationStore
{
    /// <summary>The pinned attestation, or <see langword="null" /> when this node has never approved a daemon.</summary>
    Task<DockerDaemonAttestation?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Pin <paramref name="attestation" />, replacing any previous one.</summary>
    Task WriteAsync(DockerDaemonAttestation attestation, CancellationToken cancellationToken = default);
}
