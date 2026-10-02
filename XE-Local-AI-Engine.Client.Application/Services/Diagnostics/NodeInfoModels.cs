namespace XE_Local_AI_Engine.Client.Services.Diagnostics;

/// <summary>
///     The facts a maintainer needs to read a bug report: engine build, OS, hardware, runtimes, residents, models and
///     the non-secret node settings. Carries no host name, user name or absolute path.
/// </summary>
/// <remarks>
///     Every section is best-effort: a provider that fails or does not answer in time leaves its section
///     <see langword="null" /> and adds a line to <see cref="Warnings" />.
/// </remarks>
public sealed record NodeInfo
{
    public required DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>The informational version, including the <c>+sha</c> suffix when the build embedded one.</summary>
    public required string Version { get; init; }

    /// <summary>The source commit taken from the version's <c>+sha</c> suffix, when present.</summary>
    public string? Commit { get; init; }

    /// <summary>The baked artifact flavour: <c>main</c>, <c>tester</c> or <c>dev</c>.</summary>
    public required string Flavour { get; init; }

    public required string SelectedChannel { get; init; }

    public required string DefaultChannel { get; init; }

    /// <summary>The public GitHub repository the build reports issues to, or <see langword="null" /> for an unbaked build.</summary>
    public string? RepositoryUrl { get; init; }

    public required bool IsLocalMode { get; init; }

    public required bool IsShellOwned { get; init; }

    public required string OsDescription { get; init; }

    public required string OsArchitecture { get; init; }

    public required string ProcessArchitecture { get; init; }

    public required string RuntimeFramework { get; init; }

    public string? CpuModel { get; init; }

    public int? CpuCores { get; init; }

    public long? TotalRamBytes { get; init; }

    public long? AvailableRamBytes { get; init; }

    public long? FreeDiskBytes { get; init; }

    public string? GpuVendor { get; init; }

    public string? InferenceBackend { get; init; }

    public bool? CpuFallback { get; init; }

    public IReadOnlyList<NodeInfoGpu>? Gpus { get; init; }

    public IReadOnlyList<NodeInfoGpuMemory>? LiveGpuMemory { get; init; }

    public IReadOnlyList<NodeInfoRuntime>? Runtimes { get; init; }

    /// <summary>The image and whisper daemons held in memory.</summary>
    public IReadOnlyList<NodeInfoResident>? Residents { get; init; }

    /// <summary>The running llama-server processes, one per (model, role); no port, path or probe detail.</summary>
    public IReadOnlyList<NodeInfoRunningModel>? RunningModels { get; init; }

    public IReadOnlyList<NodeInfoModel>? Models { get; init; }

    /// <summary>The reviewed, non-secret node settings, values rendered as invariant text (see <c>NodeInfoService</c>).</summary>
    public IReadOnlyDictionary<string, string?>? Settings { get; init; }

    public required long UptimeSeconds { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed record NodeInfoGpu
{
    public required string Name { get; init; }

    public long? TotalBytes { get; init; }

    public long? FreeBytes { get; init; }
}

public sealed record NodeInfoGpuMemory
{
    public required int Index { get; init; }

    public required long TotalVramBytes { get; init; }

    public required long UsedVramBytes { get; init; }

    public required long AvailableVramBytes { get; init; }
}

/// <summary>One installed native runtime; <see cref="Kind" /> is <c>llama-cpp</c>, <c>stable-diffusion-cpp</c> or <c>whisper-cpp</c>.</summary>
public sealed record NodeInfoRuntime
{
    public required string Kind { get; init; }

    public string? Tag { get; init; }

    public string? Backend { get; init; }

    public required DateTimeOffset InstalledAtUtc { get; init; }

    public string? SourceCommit { get; init; }

    public required bool IsValid { get; init; }
}

public sealed record NodeInfoResident
{
    public required string Runtime { get; init; }

    public string? ModelId { get; init; }

    public required string State { get; init; }

    public string? Backend { get; init; }
}

public sealed record NodeInfoModel
{
    public required string Name { get; init; }

    public required string Provider { get; init; }

    public long? SizeBytes { get; init; }
}

/// <summary>One running llama-server process. <see cref="State" /> is <c>responsive</c>, <c>unresponsive</c> or <c>exited</c>.</summary>
public sealed record NodeInfoRunningModel
{
    public required string ModelName { get; init; }

    /// <summary><c>chat</c>, <c>embedding</c> or <c>reranker</c>.</summary>
    public required string Role { get; init; }

    public required string State { get; init; }

    public required bool IsBusy { get; init; }

    public required bool IsTransient { get; init; }

    public DateTimeOffset? LastUsedUtc { get; init; }
}
