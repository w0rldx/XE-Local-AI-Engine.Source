namespace XE_Local_AI_Engine.Models
{
    using System.Collections.Generic;

    public sealed record ClientCapabilities
    {
        public long? RamMb { get; init; }

        public long? VramMb { get; init; }

        public bool CudaAvailable { get; init; }

        public string? GpuName { get; init; }

        public string? CpuClass { get; init; }

        public string? SystemScoreClass { get; init; }

        public IReadOnlyList<string> InstalledModels { get; init; } = [];

        public IReadOnlyList<string> SupportedCapabilities { get; init; } = [];
    }
}
