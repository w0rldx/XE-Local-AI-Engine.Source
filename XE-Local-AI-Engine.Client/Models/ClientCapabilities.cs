namespace XE_Local_AI_Engine.Client.Models;

public sealed record ClientCapabilities
{
    public long? RamMb { get; init; }

    public long? VramMb { get; init; }

    public bool CudaAvailable { get; init; }

    public string? GpuName { get; init; }

    public string? CpuClass { get; init; }

    public string? SystemScoreClass { get; init; }

    public string NodeType { get; init; } = "Local";

    public string? CloudProviderName { get; init; }

    public IReadOnlyList<string> InstalledModels { get; init; } = [];

    public IReadOnlyList<string> SupportedCapabilities { get; init; } = [];

    public string? ActiveModel { get; init; }

    public DateTimeOffset? ActiveModelExpiresAt { get; init; }
}
