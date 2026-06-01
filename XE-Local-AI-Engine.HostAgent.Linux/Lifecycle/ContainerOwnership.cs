namespace XE_Local_AI_Engine.HostAgent.Linux.Lifecycle;

using XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;

/// <summary>
///     Single source of truth for whether a container is owned (managed) by this node. Ownership is the set of
///     containers the node's static manifest declares. The stance is FAIL-CLOSED: a node booted without a manifest
///     owns NOTHING, so listing, lifecycle actions, and log streaming all agree that an unknown ownership boundary
///     denies access rather than exposing every daemon container. Production nodes always boot with a manifest, so
///     their scoping is unchanged; only a no-manifest node (e.g. the dev-fidelity harness) is affected.
/// </summary>
public static class ContainerOwnership
{
    public static bool Owns(HostAgentManifest? manifest, string containerName)
    {
        if (manifest is null)
        {
            return false;
        }

        return manifest.Containers.Any(declared => string.Equals(declared.Name, containerName, StringComparison.Ordinal));
    }
}
