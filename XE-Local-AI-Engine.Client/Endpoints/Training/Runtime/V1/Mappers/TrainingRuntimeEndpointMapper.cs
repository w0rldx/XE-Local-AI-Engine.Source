namespace XE_Local_AI_Engine.Client.Endpoints.Training.Runtime.V1.Mappers;

using XE_Local_AI_Engine.Providers.Training.Contracts;

internal static class TrainingRuntimeEndpointMapper
{
    public static TrainingRuntimeStatusResponse ToResponse(this TrainingRuntimeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return new TrainingRuntimeStatusResponse
        {
            Phase = status.Phase.ToString(),
            IsRunning = status.IsRunning,
            Terminal = status.Terminal,
            LogStartSequence = status.LogStartSequence,
            LogLines = status.LogLines,
            SanitizedError = status.SanitizedError,
            Installed = status.Installed?.ToResponse(),
            StartedAtUtc = status.StartedAtUtc?.ToUnixTimeMilliseconds(),
            CompletedAtUtc = status.CompletedAtUtc?.ToUnixTimeMilliseconds()
        };
    }

    public static TrainingRuntimePrerequisitesResponse ToResponse(this TrainingRuntimePrerequisiteReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new TrainingRuntimePrerequisitesResponse
        {
            CanInstall = report.CanInstall,
            Items = report.Items
                          .Select(static item => new TrainingRuntimePrerequisiteItemResponse
                          {
                              Key = item.Key,
                              Satisfied = item.Satisfied,
                              Detail = item.Detail
                          })
                          .ToArray()
        };
    }

    private static InstalledTrainingRuntimeResponse ToResponse(this InstalledTrainingRuntimeState state)
    {
        // The uv digest and the lockfile hash stay server-side: they are integrity inputs, not something the UI shows.
        return new InstalledTrainingRuntimeResponse
        {
            UvVersion = state.UvVersion,
            PythonVersion = state.PythonVersion,
            ContractVersion = state.ContractVersion,
            InstalledAtUtc = state.InstalledAtUtc.ToUnixTimeMilliseconds(),
            TorchVersion = state.TorchVersion,
            UnslothVersion = state.UnslothVersion,
            DeviceName = state.DeviceName
        };
    }
}
