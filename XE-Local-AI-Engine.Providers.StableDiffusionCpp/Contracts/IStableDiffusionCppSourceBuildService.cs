namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

public enum StableDiffusionCppSourceSelection
{
    Official = 0,
    Custom = 1
}

public enum StableDiffusionCppSourceRevisionMode
{
    EnginePinned = 0,
    DefaultBranch = 1,
    ExplicitCommit = 2
}

public sealed record StableDiffusionCppSourceBuildRequest(
    SdGpuBackend Backend,
    StableDiffusionCppSourceSelection Source,
    string? Repository = null,
    string? Commit = null,
    bool AcknowledgeCustomSourceRisk = false);

public sealed record StableDiffusionCppSourceBuildDescriptor(
    SdGpuBackend Backend,
    StableDiffusionCppSourceSelection Source,
    string Repository,
    StableDiffusionCppSourceRevisionMode RevisionMode,
    string? RequestedCommit,
    string? ResolvedCommit)
{
    public Guid BuildId { get; init; }
}

public enum StableDiffusionCppSourceBuildPhase
{
    Idle = 0,
    Cloning = 1,
    Verifying = 2,
    Configuring = 3,
    Building = 4,
    SmokeTesting = 5,
    Adopting = 6,
    Removing = 7,
    Completed = 8,
    Cancelled = 9,
    Failed = 10
}

public static class StableDiffusionCppSourceBuildPhaseExtensions
{
    public static string ToWireString(this StableDiffusionCppSourceBuildPhase phase)
    {
        return phase switch
        {
            StableDiffusionCppSourceBuildPhase.Idle => "idle",
            StableDiffusionCppSourceBuildPhase.Cloning => "cloning",
            StableDiffusionCppSourceBuildPhase.Verifying => "verifying",
            StableDiffusionCppSourceBuildPhase.Configuring => "configuring",
            StableDiffusionCppSourceBuildPhase.Building => "building",
            StableDiffusionCppSourceBuildPhase.SmokeTesting => "smokeTesting",
            StableDiffusionCppSourceBuildPhase.Adopting => "adopting",
            StableDiffusionCppSourceBuildPhase.Removing => "removing",
            StableDiffusionCppSourceBuildPhase.Completed => "completed",
            StableDiffusionCppSourceBuildPhase.Cancelled => "cancelled",
            StableDiffusionCppSourceBuildPhase.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown stable-diffusion.cpp source-build phase.")
        };
    }
}

public enum StableDiffusionCppSourceBuildStartOutcome
{
    Started = 0,
    AlreadyRunning = 1,
    InsufficientDisk = 2,
    MissingPrerequisites = 3,
    RuntimeBusy = 4
}

public enum StableDiffusionCppSourceBuildRemoveOutcome
{
    Removed = 0,
    NotInstalled = 1,
    RuntimeBusy = 2
}

public sealed record StableDiffusionCppSourceBuildStartResult(
    StableDiffusionCppSourceBuildStartOutcome Outcome,
    StableDiffusionCppSourceBuildPrerequisiteReport? Prerequisites = null,
    ImageRuntimeActivitySnapshot? Activity = null);

public sealed record StableDiffusionCppSourceBuildRemoveResult(
    StableDiffusionCppSourceBuildRemoveOutcome Outcome,
    ImageRuntimeActivitySnapshot? Activity = null);

public sealed record StableDiffusionCppSourceBuildStatus(
    StableDiffusionCppSourceBuildPhase Phase,
    bool IsRunning,
    bool Terminal,
    IReadOnlyList<string> LogLines,
    long LogStartSequence,
    string? SanitizedError,
    StableDiffusionCppSourceBuildDescriptor? CurrentBuild,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc)
{
    public Guid? BuildId => CurrentBuild?.BuildId;
}

/// <summary>Detached, single-flight Linux stable-diffusion.cpp source-build orchestration.</summary>
public interface IStableDiffusionCppSourceBuildService
{
    Task<StableDiffusionCppSourceBuildStartResult> StartAsync(StableDiffusionCppSourceBuildRequest request, CancellationToken ct);
    Task<StableDiffusionCppSourceBuildRemoveResult> RemoveAsync(CancellationToken ct);
    StableDiffusionCppSourceBuildStatus GetStatus();
    bool Cancel();
    Task RecoverAsync(CancellationToken ct);
    Task ShutdownAsync(CancellationToken ct);
}
