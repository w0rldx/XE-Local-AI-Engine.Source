namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>The supported llama.cpp source-build backends.</summary>
public enum LlamaCppSourceBackend
{
    Cpu = 0,
    Vulkan = 1,
    Cuda = 2
}

/// <summary>The source repository selection mode.</summary>
public enum LlamaCppSourceSelection
{
    Official = 0,
    Custom = 1
}

/// <summary>How the source revision was selected.</summary>
public enum LlamaCppSourceRevisionMode
{
    EnginePinned = 0,
    DefaultBranch = 1,
    ExplicitCommit = 2
}

/// <summary>An immutable, validated source-build request.</summary>
public sealed record LlamaCppSourceBuildRequest(
    LlamaCppSourceBackend Backend,
    LlamaCppSourceSelection Source,
    string? Repository = null,
    string? Commit = null,
    bool AcknowledgeCustomSourceRisk = false);

/// <summary>Exact provenance and revision intent for one build run.</summary>
public sealed record LlamaCppSourceBuildDescriptor(
    GpuVariant Variant,
    LlamaCppSourceSelection Source,
    string Repository,
    LlamaCppSourceRevisionMode RevisionMode,
    string? RequestedCommit,
    string? ResolvedCommit)
{
    /// <summary>Immutable identity of this concrete run, distinct even when revision intent is repeated.</summary>
    public Guid BuildId { get; init; }
}

public enum LlamaCppSourceBuildPhase
{
    Idle = 0,
    Cloning = 1,
    Verifying = 2,
    Configuring = 3,
    Building = 4,
    Adopting = 5,
    Completed = 6,
    Cancelled = 7,
    Failed = 8
}

public enum LlamaCppSourceBuildStartOutcome
{
    Started = 0,
    AlreadyRunning = 1
}

public sealed record LlamaCppSourceBuildStatus(
    LlamaCppSourceBuildPhase Phase,
    bool IsRunning,
    bool Terminal,
    IReadOnlyList<string> LogLines,
    string? SanitizedError,
    LlamaCppSourceBuildDescriptor? CurrentBuild,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc)
{
    public Guid? BuildId => CurrentBuild?.BuildId;
}

/// <summary>Single-flight generalized source-build orchestration.</summary>
public interface ILlamaCppSourceBuildService
{
    Task<LlamaCppSourceBuildStartOutcome> StartAsync(LlamaCppSourceBuildRequest request, CancellationToken ct);
    LlamaCppSourceBuildStatus GetStatus();
    bool Cancel();
    bool CancelLegacyPinnedCuda();
    Task RecoverAsync(CancellationToken ct);
}
