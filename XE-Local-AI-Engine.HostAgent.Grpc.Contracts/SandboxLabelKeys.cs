namespace XE_Local_AI_Engine.HostAgent.Grpc.Contracts;

/// <summary>
///     The reserved Docker label keys an AgentHome sandbox container is stamped with (Marker J-local plan §4.2,
///     §6.2.1 rule 15). Defined once here so both the worker-side <c>LocalContainerSandboxProvider</c> and the
///     HostAgent-side <c>SandboxRuntimeService</c> reference the same strings — a single spelling avoids the two
///     containers carrying divergent label sets. The service is authoritative: it rebuilds these labels from the
///     attach-key message on create and re-reads them on attach to validate that owner/node/profile/manifest match.
/// </summary>
public static class SandboxLabelKeys
{
    /// <summary>The sandbox owner's user id (<c>SandboxAttachKey.OwnerUserId</c>).</summary>
    public const string Owner = "c0re.agent-home.owner";

    /// <summary>The node the sandbox belongs to (<c>SandboxAttachKey.NodeId</c>).</summary>
    public const string Node = "c0re.agent-home.node";

    /// <summary>The runtime profile the sandbox was created with (<c>SandboxAttachKey.RuntimeProfile</c>).</summary>
    public const string Profile = "c0re.agent-home.profile";

    /// <summary>The AgentHome manifest version in force at create (<c>SandboxAttachKey.ManifestVersion</c>).</summary>
    public const string Manifest = "c0re.agent-home.manifest";

    /// <summary>The deterministic container name the sandbox was created under.</summary>
    public const string Name = "c0re.agent-home.name";
}
