namespace XE_Local_AI_Engine.Client.Models;

public sealed record ClientCapabilities
{
    public int SchemaVersion { get; init; } = 2;

    public long? RamMb { get; init; }

    public long? VramMb { get; init; }

    public bool CudaAvailable { get; init; }

    public string? GpuName { get; init; }

    public string? CpuClass { get; init; }

    public string? SystemScoreClass { get; init; }

    public string NodeType { get; init; } = "Local";

    public string? CloudProviderName { get; init; }

    public bool? OllamaReachable { get; init; }

    public string? OllamaVersion { get; init; }

    public string ManagementMode { get; init; } = "unknown";

    public DateTimeOffset? LastCapabilityReportAt { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    public IReadOnlyList<string> InstalledModels { get; init; } = [];

    public IReadOnlyList<ClientModelMetadata> InstalledModelMetadata { get; init; } = [];

    public IReadOnlyList<string> SupportedCapabilities { get; init; } = [];

    public string? ActiveModel { get; init; }

    public DateTimeOffset? ActiveModelExpiresAt { get; init; }

    public int MaxMessageRequestTimeoutSeconds { get; init; } = 300;
}

public sealed record ClientModelMetadata
{
    public required string Name { get; init; }

    public string? Digest { get; init; }

    public int? MaxContextTokens { get; init; }
}
