namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public interface IBenchmarkRuntimeSnapshotFactory
{
    BenchmarkRuntimeSnapshotV1 Create(BenchmarkRuntimeSnapshotInput input);
    byte[] Serialize(BenchmarkRuntimeSnapshotV1 snapshot);
    BenchmarkRuntimeSnapshotV1 Deserialize(ReadOnlySpan<byte> payload);
}

public sealed record BenchmarkRuntimeSnapshotInput
{
    public required Guid ProjectId { get; init; }

    public required Guid AgentDefinitionId { get; init; }

    public required long AgentVersion { get; init; }

    public required string CoreTask { get; init; }

    public required int RequestedContextTokens { get; init; }

    public required ResolvedAgentRuntime ResolvedRuntime { get; init; }

    public required BenchmarkLlamaRuntimeSnapshotV1 PrimaryRuntime { get; init; }

    public required BenchmarkSamplingSnapshotV1 PrimarySampling { get; init; }

    public required BenchmarkInstalledModelSnapshotV1 PrimaryModel { get; init; }

    public required BenchmarkFreezeDependencySetV1 Dependencies { get; init; }

    public required string ApplicationVersion { get; init; }

    public required long CreatedAtUtc { get; init; }
}

/// <summary>
///     What a run was frozen with. Primary-only: judging is defined by the project's judge policy revision and frozen
///     per attempt, so nothing about it belongs to a run that can be judged many times.
/// </summary>
public sealed record BenchmarkRuntimeSnapshotV1(
    int SchemaVersion,
    Guid ProjectId,
    Guid AgentDefinitionId,
    long AgentVersion,
    string CoreTask,
    int RequestedContextTokens,
    ResolvedAgentRuntime ResolvedRuntime,
    BenchmarkLlamaRuntimeSnapshotV1 PrimaryRuntime,
    BenchmarkSamplingSnapshotV1 PrimarySampling,
    BenchmarkInstalledModelSnapshotV1 PrimaryModel,
    BenchmarkFreezeDependencySetV1 Dependencies,
    string ApplicationVersion,
    long CreatedAtUtc,
    string ConfigurationHash);

public sealed record BenchmarkInstalledModelSnapshotV1(
    string ModelName,
    string RegistryRevision,
    IReadOnlyList<BenchmarkRegistryAliasSnapshotV1> RegistryAliases,
    string RegistryAliasSetHash,
    IReadOnlyList<BenchmarkPhysicalMemberSnapshotV1> Members,
    string PhysicalMemberSetHash,
    LocalModelOrigin? Origin,
    string ProviderName,
    string? ProviderMappingRevision,
    string? RepositoryId,
    string? RepositoryRevision,
    string? SourceFileName,
    string? Quantization,
    string? Role,
    string ModelContentFingerprint);

public sealed record BenchmarkRegistryAliasSnapshotV1(string ModelName, string RegistryRevision);

public sealed record BenchmarkPhysicalMemberSnapshotV1(
    string RelativePath,
    InstalledModelPhysicalMemberRole Role,
    long SizeBytes,
    string Sha256,
    IReadOnlyList<string> OwningAliases,
    bool Required,
    int? MetadataSchemaVersion,
    string? MemberFingerprint);

public sealed record BenchmarkLlamaRuntimeSnapshotV1(
    GpuVariant Variant,
    int ContextTokens,
    int? GpuLayers,
    string? TensorSplit,
    string? OverrideTensor,
    string? KvTypeK,
    string? KvTypeV,
    bool FlashAttention,
    LlamaServerBenchmarkLaunchPolicy LaunchPolicy)
{
    public ResolvedLaunchArguments ToResolvedLaunchArguments() =>
        ResolvedLaunchArguments.Replay(ContextTokens,
            GpuLayers,
            TensorSplit,
            OverrideTensor,
            KvTypeK,
            KvTypeV,
            FlashAttention);
}

/// <summary>The frozen sampling a run replays.</summary>
/// <remarks>
///     EVERY MEMBER ADDED HERE MUST BE NULLABLE AND <see cref="JsonIgnoreCondition.WhenWritingNull" />. The factory
///     serializes with <see cref="JsonIgnoreCondition.Never" /> and validates a stored payload by RE-HASHING it, so a
///     member that emits <c>null</c> changes the bytes of every row frozen before it existed and every one of those
///     runs stops replaying with "configuration hash is invalid". Omitting a null member keeps a legacy payload
///     byte-identical. <c>BenchmarkRuntimeSnapshotV1CompatibilityTests</c> is the guard.
/// </remarks>
/// <param name="ReasoningBudgetTokens">
///     The per-request thinking budget (<c>reasoning_budget_tokens</c>), or <see langword="null" /> for reasoning bounded only by the effort ladder and the window.
/// </param>
/// <param name="ReasoningBudgetEnforceable">
///     Whether llama-server can ENFORCE that budget, frozen. <see langword="null" /> on a pre-member run reads as the
///     inert <see langword="true" />: never remove a cap that was working.
/// </param>
public sealed record BenchmarkSamplingSnapshotV1(
    // double, unlike its neighbours: the one member duplicated onto a plaintext run column and exported, where a float widened into it reads back as 0.699999988079071 for a temperature of
    // 0.7. The sampler still takes a float; SamplingOptions narrows it at the provider boundary. Serialized bytes are unchanged — 0.7 is 0.7 either way, so no stored snapshot re-hashes.
    double? Temperature,
    float? TopP,
    int? TopK,
    float? MinP,
    int? MaxOutputTokens,
    float? RepeatPenalty,
    int? RepeatLastN,
    float? PresencePenalty,
    float? FrequencyPenalty,
    IReadOnlyList<string> Stop,
    string SeedPolicy,
    string? SeedValue,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? ReasoningBudgetTokens = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? ReasoningBudgetEnforceable = null);

public sealed record BenchmarkFreezeDependencySetV1(
    string AgentDependencyHash,
    string PlaybookCohortHash,
    string SkillAssignmentSetHash,
    string CustomToolAssignmentSetHash,
    string PrimaryRuntimeConfigurationHash,
    string? JudgeRuntimeConfigurationHash);

public sealed class BenchmarkSnapshotException : InvalidOperationException
{
    public BenchmarkSnapshotException(string message)
        : base(message)
    {
    }

    public BenchmarkSnapshotException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
