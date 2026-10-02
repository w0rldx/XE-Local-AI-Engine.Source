namespace XE_Local_AI_Engine.Client.Endpoints.Diagnostics.V1;

/// <summary>
///     Response for <c>GET api/local/v1/diagnostics/node-info</c>: engine build, OS, hardware, runtimes, residents,
///     installed models and the reviewed node settings. Carries no host name, user name or absolute path.
/// </summary>
/// <remarks>
///     Sections are best-effort: an unavailable one is <see langword="null" /> and named in <see cref="Warnings" />.
///     <see cref="Settings" /> values are invariant text (booleans and numbers as JSON literals, lists and objects as
///     compact JSON); <c>MachineKey</c>, <c>OllamaEndpoint</c> and <c>WebSearchSearxngUrl</c> are never included.
/// </remarks>
public sealed record NodeInfoResponse
{
    public required DateTimeOffset CapturedAtUtc { get; init; }

    public required string Version { get; init; }

    public required string? Commit { get; init; }

    public required string Flavour { get; init; }

    public required string SelectedChannel { get; init; }

    public required string DefaultChannel { get; init; }

    public required string? RepositoryUrl { get; init; }

    public required bool IsLocalMode { get; init; }

    public required bool IsShellOwned { get; init; }

    public required bool VerboseLogging { get; init; }

    public required string OsDescription { get; init; }

    public required string OsArchitecture { get; init; }

    public required string ProcessArchitecture { get; init; }

    public required string RuntimeFramework { get; init; }

    public required string? CpuModel { get; init; }

    public required int? CpuCores { get; init; }

    public required long? TotalRamBytes { get; init; }

    public required long? AvailableRamBytes { get; init; }

    public required long? FreeDiskBytes { get; init; }

    public required string? GpuVendor { get; init; }

    public required string? InferenceBackend { get; init; }

    public required bool? CpuFallback { get; init; }

    public required IReadOnlyList<NodeInfoGpuResponse>? Gpus { get; init; }

    public required IReadOnlyList<NodeInfoGpuMemoryResponse>? LiveGpuMemory { get; init; }

    public required IReadOnlyList<NodeInfoRuntimeResponse>? Runtimes { get; init; }

    public required IReadOnlyList<NodeInfoResidentResponse>? Residents { get; init; }

    public required IReadOnlyList<NodeInfoRunningModelResponse>? RunningModels { get; init; }

    public required IReadOnlyList<NodeInfoModelResponse>? Models { get; init; }

    public required IReadOnlyDictionary<string, string?>? Settings { get; init; }

    public required long UptimeSeconds { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed record NodeInfoGpuResponse
{
    public required string Name { get; init; }

    public required long? TotalBytes { get; init; }

    public required long? FreeBytes { get; init; }
}

public sealed record NodeInfoGpuMemoryResponse
{
    public required int Index { get; init; }

    public required long TotalVramBytes { get; init; }

    public required long UsedVramBytes { get; init; }

    public required long AvailableVramBytes { get; init; }
}

/// <summary>One installed native runtime; <see cref="Kind" /> is <c>llama-cpp</c>, <c>stable-diffusion-cpp</c> or <c>whisper-cpp</c>.</summary>
public sealed record NodeInfoRuntimeResponse
{
    public required string Kind { get; init; }

    public required string? Tag { get; init; }

    public required string? Backend { get; init; }

    public required DateTimeOffset InstalledAtUtc { get; init; }

    public required string? SourceCommit { get; init; }

    public required bool IsValid { get; init; }
}

public sealed record NodeInfoResidentResponse
{
    public required string Runtime { get; init; }

    public required string? ModelId { get; init; }

    public required string State { get; init; }

    public required string? Backend { get; init; }
}

public sealed record NodeInfoModelResponse
{
    public required string Name { get; init; }

    public required string Provider { get; init; }

    public required long? SizeBytes { get; init; }
}

/// <summary>One running llama-server process; <see cref="State" /> is <c>responsive</c>, <c>unresponsive</c> or <c>exited</c>.</summary>
public sealed record NodeInfoRunningModelResponse
{
    public required string ModelName { get; init; }

    public required string Role { get; init; }

    public required string State { get; init; }

    public required bool IsBusy { get; init; }

    public required bool IsTransient { get; init; }

    public required DateTimeOffset? LastUsedUtc { get; init; }
}
