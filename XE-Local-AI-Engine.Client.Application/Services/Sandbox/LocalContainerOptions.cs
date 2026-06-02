namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Configuration for the <c>local-container</c> sandbox provider. Bound
///     from the <c>LocalContainer</c> section. These values shape the sandbox-container the provider asks HostAgent to
///     create (image, resource ceiling, network posture) and bound the whole-file copy-into transfer
///     (<see cref="MaxCopyFileBytes" />). The provider itself is Docker-free — it is a thin
///     gRPC client to HostAgent, which owns the privileged Docker work — so no Docker SDK type appears here.
/// </summary>
public sealed record LocalContainerOptions
{
    public const string SectionName = "LocalContainer";

    /// <summary>The default per-file copy ceiling (64 MiB). A file over this is skipped and logged, never truncated.</summary>
    public const long DefaultMaxCopyFileBytes = 64L * 1024 * 1024;

    /// <summary>The container engine. The current local HostAgent runtime supports <c>Docker</c>.</summary>
    public string Engine { get; init; } = "Docker";

    /// <summary>Reserved no-op flag: the HostAgent Docker client is API-based, not CLI-based.</summary>
    public bool UseCli { get; init; }

    /// <summary>Optional Docker host override. Null lets HostAgent use its configured rootless socket.</summary>
    public string? DockerHost { get; init; }

    /// <summary>The image the sandbox container is created from (a locally built tag with no registry digest).</summary>
    public string DefaultImage { get; init; } = "dotnet-agent-home:2026-05-agenthome-mvp";

    /// <summary>The prefix of the deterministic per-owner/node container name.</summary>
    public string ContainerNamePrefix { get; init; } = "c0re-agent-home";

    /// <summary>The requested network posture; the secure default is no network.</summary>
    public string NetworkMode { get; init; } = "none";

    /// <summary>Maximum CPU cores the sandbox may use.</summary>
    public double CpuLimit { get; init; } = 2.0;

    /// <summary>Maximum resident memory in megabytes.</summary>
    public int MemoryLimitMb { get; init; } = 4096;

    /// <summary>Maximum number of processes/threads inside the sandbox.</summary>
    public int PidsLimit { get; init; } = 512;

    /// <summary>The per-file copy-into ceiling in bytes. Defaults to 64 MiB.</summary>
    public long MaxCopyFileBytes { get; init; } = DefaultMaxCopyFileBytes;
}
