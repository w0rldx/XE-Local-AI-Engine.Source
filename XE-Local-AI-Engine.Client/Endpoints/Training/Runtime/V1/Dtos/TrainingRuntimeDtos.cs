namespace XE_Local_AI_Engine.Client.Endpoints.Training.Runtime.V1;

public sealed class TrainingRuntimePrerequisiteItemResponse
{
    public required string Key { get; init; }
    public required bool Satisfied { get; init; }
    public required string Detail { get; init; }
}

public sealed class TrainingRuntimePrerequisitesResponse
{
    public required bool CanInstall { get; init; }
    public required IReadOnlyList<TrainingRuntimePrerequisiteItemResponse> Items { get; init; }
}

public sealed class InstalledTrainingRuntimeResponse
{
    public required string UvVersion { get; init; }
    public required string PythonVersion { get; init; }
    public required int ContractVersion { get; init; }
    public required long InstalledAtUtc { get; init; }
    public string? TorchVersion { get; init; }
    public string? UnslothVersion { get; init; }
    public string? DeviceName { get; init; }
}

public sealed class TrainingRuntimeStatusResponse
{
    public required string Phase { get; init; }
    public required bool IsRunning { get; init; }
    public required bool Terminal { get; init; }
    public required long LogStartSequence { get; init; }
    public required IReadOnlyList<string> LogLines { get; init; }
    public string? SanitizedError { get; init; }
    public InstalledTrainingRuntimeResponse? Installed { get; init; }
    public long? StartedAtUtc { get; init; }
    public long? CompletedAtUtc { get; init; }
}

public sealed class StartTrainingRuntimeInstallResponse
{
    public required bool Started { get; init; }
    public required TrainingRuntimeStatusResponse Status { get; init; }
}

public sealed class TrainingRuntimeBlockedResponse
{
    public required string Reason { get; init; }
    public required string Message { get; init; }
    public TrainingRuntimePrerequisitesResponse? Prerequisites { get; init; }
}
