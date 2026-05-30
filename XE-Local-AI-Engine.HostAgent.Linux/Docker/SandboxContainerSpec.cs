namespace XE_Local_AI_Engine.HostAgent.Linux.Docker;

/// <summary>
///     Describes a dedicated AgentHome sandbox container to create (Marker J-local plan §4.1, D5). This is a
///     hardened create path distinct from <see cref="IDockerRuntimeClient.EnsureContainerAsync" /> (the managed
///     runtime path): it carries resource ceilings, the network posture, the non-root user, and attach-validation
///     labels (owner/node/profile/manifest) applied to the container's <c>HostConfig</c> and labels.
/// </summary>
public sealed record SandboxContainerSpec
{
    /// <summary>Deterministic container name (the provider derives it from node + owner hash).</summary>
    public required string Name { get; init; }

    /// <summary>The local sandbox image to run (a tag-only local image is accepted — D6, not routed through the strict managed-runtime <c>DockerImageReference</c>).</summary>
    public required string Image { get; init; }

    /// <summary>Maximum CPU cores; mapped to <c>NanoCPUs</c>. <see langword="null" /> leaves the limit unset.</summary>
    public double? CpuCount { get; init; }

    /// <summary>Maximum resident memory in megabytes; mapped to <c>Memory</c>. <see langword="null" /> leaves the limit unset.</summary>
    public int? MemoryMb { get; init; }

    /// <summary>Maximum number of processes/threads; mapped to <c>PidsLimit</c>. <see langword="null" /> leaves the limit unset.</summary>
    public int? PidsLimit { get; init; }

    /// <summary>The network posture; the secure default is <see cref="SandboxNetworkMode.None" /> (no network).</summary>
    public SandboxNetworkMode NetworkMode { get; init; } = SandboxNetworkMode.None;

    /// <summary>Attach-validation labels (owner/node/profile/manifest) stamped on the container.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Environment variables for the container's entry process.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The non-root user the container runs as (e.g. <c>"1000:1000"</c> or an image user name); empty uses the image default.</summary>
    public string User { get; init; } = string.Empty;
}
