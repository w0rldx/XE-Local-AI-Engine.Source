namespace XE_Local_AI_Engine.HostAgent.Linux.Docker;

/// <summary>
///     Describes a single one-shot utility container run. This is a hardened, run-to-completion path
///     distinct from the sandbox/managed-runtime paths: the container runs the supplied <see cref="Arguments" /> argv
///     against a digest-pinned image, the run is awaited for exit with a timeout, output is captured, and the container
///     is removed afterwards (unless the run failed and <see cref="RetainOnFailure" /> is set). The argv is built
///     server-side from a fixed command profile — never accepted from the wire — so this spec carries an already-built
///     argv, never a shell string. NEVER mounts the Docker socket or host binds.
/// </summary>
public sealed record UtilityContainerRunSpec
{
    /// <summary>The pinned image reference to run (canonical <c>repo:tag@sha256</c> form). Pulled if absent.</summary>
    public required string Image { get; init; }

    /// <summary>The built argv passed to the image (the image ENTRYPOINT is <c>llmfit</c>, so this is the args tail).</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Environment variables for the run.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The managed runtime network to attach to; <see langword="null" /> or empty maps to <c>--network none</c>.</summary>
    public string? NetworkName { get; init; }

    /// <summary>Maximum CPU cores; mapped to <c>NanoCPUs</c>. <see langword="null" /> leaves the limit unset.</summary>
    public double? CpuCount { get; init; }

    /// <summary>Maximum resident memory in megabytes; mapped to <c>Memory</c>. <see langword="null" /> leaves the limit unset.</summary>
    public int? MemoryMb { get; init; }

    /// <summary>Maximum number of processes/threads; mapped to <c>PidsLimit</c>. <see langword="null" /> leaves the limit unset.</summary>
    public int? PidsLimit { get; init; }

    /// <summary>The non-root user the container runs as; empty uses the image default.</summary>
    public string User { get; init; } = string.Empty;

    /// <summary>When the run fails (non-zero exit), keep the container for debugging instead of removing it. Development only.</summary>
    public bool RetainOnFailure { get; init; }
}
