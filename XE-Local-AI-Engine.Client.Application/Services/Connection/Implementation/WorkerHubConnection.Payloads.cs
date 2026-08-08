namespace XE_Local_AI_Engine.Client.Services.Connection.Implementation;

using XE_Local_AI_Engine.Client.Models;

public sealed partial class WorkerHubConnection
{
    private sealed record ClientCapabilitiesPayload
    {
        public required HardwareCapabilitiesPayload HardwareInfo { get; init; }

        public required SystemCapabilitiesPayload Capabilities { get; init; }

        public string NodeType { get; init; } = "Local";

        public string? CloudProviderName { get; init; }

        public required NodeSettingsPayload Settings { get; init; }

        public static ClientCapabilitiesPayload From(ClientCapabilities capabilities)
        {
            return new ClientCapabilitiesPayload
            {
                HardwareInfo = new HardwareCapabilitiesPayload
                {
                    RamMb = ToInt32(capabilities.RamMb),
                    VramMb = ToInt32(capabilities.VramMb),
                    CudaAvailable = capabilities.CudaAvailable,
                    GpuName = capabilities.GpuName,
                    CpuClass = capabilities.CpuClass
                },
                Capabilities = new SystemCapabilitiesPayload
                {
                    SchemaVersion = capabilities.SchemaVersion,
                    SystemScoreClass = capabilities.SystemScoreClass ?? "Medium",
                    OllamaReachable = capabilities.OllamaReachable,
                    OllamaVersion = capabilities.OllamaVersion,
                    ManagementMode = capabilities.ManagementMode,
                    LastCapabilityReportAt = capabilities.LastCapabilityReportAt,
                    Diagnostics = capabilities.Diagnostics,
                    InstalledModels = capabilities.InstalledModels,
                    InstalledModelMetadata = capabilities.InstalledModelMetadata.Select(ModelMetadataPayload.From).ToArray(),
                    SupportedCapabilities = capabilities.SupportedCapabilities,
                    ActiveModel = capabilities.ActiveModel,
                    ActiveModelExpiresAt = capabilities.ActiveModelExpiresAt
                },
                NodeType = capabilities.NodeType,
                CloudProviderName = capabilities.CloudProviderName,
                Settings = new NodeSettingsPayload
                {
                    MaxMessageRequestTimeoutSeconds = capabilities.MaxMessageRequestTimeoutSeconds
                }
            };
        }

        private static int ToInt32(long? value)
        {
            return value is null ? 0 : checked((int)value.Value);
        }
    }

    private sealed record HardwareCapabilitiesPayload
    {
        public int RamMb { get; init; }

        public int VramMb { get; init; }

        public bool CudaAvailable { get; init; }

        public string? GpuName { get; init; }

        public string? CpuClass { get; init; }
    }

    private sealed record SystemCapabilitiesPayload
    {
        public int SchemaVersion { get; init; } = 2;

        public string SystemScoreClass { get; init; } = "Medium";

        public bool? OllamaReachable { get; init; }

        public string? OllamaVersion { get; init; }

        public string ManagementMode { get; init; } = "unknown";

        public DateTimeOffset? LastCapabilityReportAt { get; init; }

        public IReadOnlyList<string> Diagnostics { get; init; } = [];

        public IReadOnlyList<string> InstalledModels { get; init; } = [];

        public IReadOnlyList<ModelMetadataPayload> InstalledModelMetadata { get; init; } = [];

        public IReadOnlyList<string> SupportedCapabilities { get; init; } = [];

        public string? ActiveModel { get; init; }

        public DateTimeOffset? ActiveModelExpiresAt { get; init; }
    }

    private sealed record ModelMetadataPayload
    {
        public required string Name { get; init; }

        public string? Digest { get; init; }

        public int? MaxContextTokens { get; init; }

        public static ModelMetadataPayload From(ClientModelMetadata metadata)
        {
            return new ModelMetadataPayload
            {
                Name = metadata.Name,
                Digest = metadata.Digest,
                MaxContextTokens = metadata.MaxContextTokens
            };
        }
    }

    private sealed record NodeSettingsPayload
    {
        public int MaxMessageRequestTimeoutSeconds { get; init; }
    }
}
