namespace XE_Local_AI_Engine.Client.Endpoints.RuntimeManager.V1;

using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

/// <summary>
///     Response DTO for runtime manager status operations.
/// </summary>
public sealed class RuntimeManagerStatusResponse
{
    public required HostAgentStatusDto Status { get; init; }

    public required HostCapabilitiesDto Capabilities { get; init; }

    public required IReadOnlyList<RuntimeComponentStatusDto> Components { get; init; }

    public required RuntimeModelProviderHealthResponse ModelProviderHealth { get; init; }

    public required IReadOnlyList<RuntimeLocalModelResponse> Models { get; init; }

    public required RuntimeManifestResponse Manifest { get; init; }
}

/// <summary>
///     Request DTO for runtime container action operations.
/// </summary>
public sealed class RuntimeContainerActionRequest
{
    public string? ContainerName { get; init; }

    public string? Action { get; init; }

    public int? DrainTimeoutSeconds { get; init; }
}

/// <summary>
///     Response DTO for runtime container action operations.
/// </summary>
public sealed class RuntimeContainerActionResponse
{
    public required string ContainerName { get; init; }

    public required string Action { get; init; }

    public required bool Succeeded { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required IReadOnlyList<RuntimeComponentStatusDto> Components { get; init; }

    public required IReadOnlyList<string> Diagnostics { get; init; }
}

/// <summary>
///     Request DTO for runtime logs operations.
/// </summary>
public sealed class RuntimeLogsRequest
{
    public string? ContainerName { get; init; }

    public int? TailLines { get; init; }

    public bool Follow { get; init; } = true;
}

/// <summary>
///     Response DTO for runtime log line operations.
/// </summary>
public sealed class RuntimeLogLineResponse
{
    public required string ContainerName { get; init; }

    public required string Stream { get; init; }

    public required string Line { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }
}

/// <summary>
///     Response DTO for runtime model provider health operations.
/// </summary>
public sealed class RuntimeModelProviderHealthResponse
{
    public required string ProviderName { get; init; }

    public required bool IsHealthy { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    public required IReadOnlyList<string> Diagnostics { get; init; }
}

/// <summary>
///     Response DTO for runtime local model operations.
/// </summary>
public sealed class RuntimeLocalModelResponse
{
    public required string ModelName { get; init; }

    public required string ProviderName { get; init; }

    public required bool IsAvailable { get; init; }

    public long? SizeBytes { get; init; }

    public DateTimeOffset? ModifiedAt { get; init; }

    public int? MaxContextTokens { get; init; }
}

/// <summary>
///     Response DTO for runtime manifest operations.
/// </summary>
public sealed class RuntimeManifestResponse
{
    public required bool Available { get; init; }

    public int? SchemaVersion { get; init; }

    public required string RuntimeMode { get; init; }

    public required string BootstrapModel { get; init; }

    public required string DefaultChatModel { get; init; }

    public int? MaxRuntimeDiskGb { get; init; }

    public int? StopDrainTimeoutSeconds { get; init; }

    public required IReadOnlyList<RuntimeManifestContainerResponse> Containers { get; init; }

    public required IReadOnlyList<string> Diagnostics { get; init; }
}

/// <summary>
///     Response DTO for runtime manifest container operations.
/// </summary>
public sealed class RuntimeManifestContainerResponse
{
    public required string Name { get; init; }

    public required string Image { get; init; }

    public required string Network { get; init; }

    public required IReadOnlyList<RuntimeManifestEnvironmentResponse> Environment { get; init; }

    public required IReadOnlyList<RuntimeManifestVolumeResponse> Volumes { get; init; }
}

/// <summary>
///     Response DTO for runtime manifest environment operations.
/// </summary>
public sealed class RuntimeManifestEnvironmentResponse
{
    public required string Name { get; init; }

    public required string Value { get; init; }
}

/// <summary>
///     Response DTO for runtime manifest volume operations.
/// </summary>
public sealed class RuntimeManifestVolumeResponse
{
    public required string Source { get; init; }

    public required string Target { get; init; }

    public required bool ReadOnly { get; init; }
}

internal static class RuntimeManagerEndpointDtoMapper
{
    public static RuntimeLogLineResponse ToResponse(this HostAgentLogLineDto line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new RuntimeLogLineResponse
        {
            ContainerName = line.ContainerName,
            Stream = line.Stream,
            Line = line.Line,
            ObservedAt = line.ObservedAt
        };
    }
}
