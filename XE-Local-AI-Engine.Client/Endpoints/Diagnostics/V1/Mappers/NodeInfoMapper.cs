namespace XE_Local_AI_Engine.Client.Endpoints.Diagnostics.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.Diagnostics;

internal static class NodeInfoMapper
{
    public static NodeInfoResponse ToResponse(this NodeInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new NodeInfoResponse
        {
            CapturedAtUtc = info.CapturedAtUtc,
            Version = info.Version,
            Commit = info.Commit,
            Flavour = info.Flavour,
            SelectedChannel = info.SelectedChannel,
            DefaultChannel = info.DefaultChannel,
            RepositoryUrl = info.RepositoryUrl,
            IsLocalMode = info.IsLocalMode,
            IsShellOwned = info.IsShellOwned,
            VerboseLogging = info.VerboseLogging,
            OsDescription = info.OsDescription,
            OsArchitecture = info.OsArchitecture,
            ProcessArchitecture = info.ProcessArchitecture,
            RuntimeFramework = info.RuntimeFramework,
            CpuModel = info.CpuModel,
            CpuCores = info.CpuCores,
            TotalRamBytes = info.TotalRamBytes,
            AvailableRamBytes = info.AvailableRamBytes,
            FreeDiskBytes = info.FreeDiskBytes,
            GpuVendor = info.GpuVendor,
            InferenceBackend = info.InferenceBackend,
            CpuFallback = info.CpuFallback,
            Gpus = info.Gpus?.Select(static gpu => new NodeInfoGpuResponse
            {
                Name = gpu.Name,
                TotalBytes = gpu.TotalBytes,
                FreeBytes = gpu.FreeBytes
            }).ToArray(),
            LiveGpuMemory = info.LiveGpuMemory?.Select(static gpu => new NodeInfoGpuMemoryResponse
            {
                Index = gpu.Index,
                TotalVramBytes = gpu.TotalVramBytes,
                UsedVramBytes = gpu.UsedVramBytes,
                AvailableVramBytes = gpu.AvailableVramBytes
            }).ToArray(),
            Runtimes = info.Runtimes?.Select(static runtime => new NodeInfoRuntimeResponse
            {
                Kind = runtime.Kind,
                Tag = runtime.Tag,
                Backend = runtime.Backend,
                InstalledAtUtc = runtime.InstalledAtUtc,
                SourceCommit = runtime.SourceCommit,
                IsValid = runtime.IsValid
            }).ToArray(),
            Residents = info.Residents?.Select(static resident => new NodeInfoResidentResponse
            {
                Runtime = resident.Runtime,
                ModelId = resident.ModelId,
                State = resident.State,
                Backend = resident.Backend
            }).ToArray(),
            RunningModels = info.RunningModels?.Select(static process => new NodeInfoRunningModelResponse
            {
                ModelName = process.ModelName,
                Role = process.Role,
                State = process.State,
                IsBusy = process.IsBusy,
                IsTransient = process.IsTransient,
                LastUsedUtc = process.LastUsedUtc
            }).ToArray(),
            Models = info.Models?.Select(static model => new NodeInfoModelResponse
            {
                Name = model.Name,
                Provider = model.Provider,
                SizeBytes = model.SizeBytes
            }).ToArray(),
            Settings = info.Settings,
            UptimeSeconds = info.UptimeSeconds,
            Warnings = info.Warnings
        };
    }
}
