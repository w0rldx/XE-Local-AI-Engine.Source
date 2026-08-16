namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Models;
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

public sealed record BenchmarkRuntimeSnapshotInput(
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
    long CreatedAtUtc);

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

public sealed record BenchmarkSamplingSnapshotV1(
    float? Temperature,
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
    string? SeedValue);

public static class BenchmarkFrozenPolicies
{
    public const string FixedSeedPolicy = "fixed";

    public static BenchmarkSamplingSnapshotV1 DeterministicSampling() =>
        new(0, null, null, null, null, null, null, null, null, [], FixedSeedPolicy, "0");

    /// <summary>
    /// The timeout policy every V1 snapshot was executed with, pinned here because the node-level
    /// <see cref="TimeoutSettings.InvocationTimeoutSeconds"/> default has since moved. A frozen run therefore replays
    /// identically across app versions instead of silently inheriting whatever the package builder defaults to.
    /// follow-up: fold timeouts into a versioned snapshot so a future change is visible in the configuration hash.
    /// </summary>
    public static TimeoutSettings FrozenTimeouts() =>
        new()
        {
            InvocationTimeoutSeconds = 300,
            ToolCallTimeoutSeconds = 30,
            StreamIdleTimeoutSeconds = 60
        };

}

public sealed record BenchmarkFreezeDependencySetV1(
    string AgentDependencyHash,
    string PlaybookCohortHash,
    string SkillAssignmentSetHash,
    string CustomToolAssignmentSetHash,
    string PrimaryRuntimeConfigurationHash,
    string? JudgeRuntimeConfigurationHash);

public sealed class BenchmarkRuntimeSnapshotFactory(IBenchmarkEligibilityPolicy eligibilityPolicy) : IBenchmarkRuntimeSnapshotFactory
{
    private static readonly SearchValues<char> LowerHexCharacters = SearchValues.Create("0123456789abcdef");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false
    };

    private readonly IBenchmarkEligibilityPolicy _eligibilityPolicy = eligibilityPolicy ?? throw new ArgumentNullException(nameof(eligibilityPolicy));

    public BenchmarkRuntimeSnapshotV1 Create(BenchmarkRuntimeSnapshotInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var eligibleRuntime = _eligibilityPolicy.Apply(input.ResolvedRuntime);
        ValidateModel(input.PrimaryModel);
        ValidateRuntime(input.PrimaryRuntime, input.RequestedContextTokens);
        ValidateSampling(input.PrimarySampling);
        var unhashed = new BenchmarkRuntimeSnapshotV1(1, input.ProjectId, input.AgentDefinitionId, input.AgentVersion,
            input.CoreTask, input.RequestedContextTokens, eligibleRuntime, input.PrimaryRuntime, input.PrimarySampling, input.PrimaryModel, input.Dependencies,
            input.ApplicationVersion, input.CreatedAtUtc, string.Empty);
        return unhashed with
        {
            ConfigurationHash = ComputeHash(unhashed)
        };
    }

    public byte[] Serialize(BenchmarkRuntimeSnapshotV1 snapshot)
    {
        Validate(snapshot);
        return JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
    }

    public BenchmarkRuntimeSnapshotV1 Deserialize(ReadOnlySpan<byte> payload)
    {
        var snapshot = JsonSerializer.Deserialize<BenchmarkRuntimeSnapshotV1>(payload, JsonOptions)
                       ?? throw new BenchmarkSnapshotException("Benchmark snapshot payload is empty.");
        Validate(snapshot);
        return snapshot;
    }

    private void Validate(BenchmarkRuntimeSnapshotV1 snapshot)
    {
        if (snapshot.SchemaVersion != 1)
        {
            throw new BenchmarkSnapshotException("Benchmark snapshot schema is not supported.");
        }

        _ = _eligibilityPolicy.Apply(snapshot.ResolvedRuntime);
        ValidateRuntime(snapshot.PrimaryRuntime, snapshot.RequestedContextTokens);
        ValidateSampling(snapshot.PrimarySampling);
        ValidateModel(snapshot.PrimaryModel);
        var expected = ComputeHash(snapshot with
        {
            ConfigurationHash = string.Empty
        });
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(snapshot.ConfigurationHash)))
        {
            throw new BenchmarkSnapshotException("Benchmark snapshot configuration hash is invalid.");
        }
    }

    private static void ValidateRuntime(BenchmarkLlamaRuntimeSnapshotV1 runtime, int minimumContextTokens)
    {
        try
        {
            _ = runtime.ToResolvedLaunchArguments();
        }
        catch (ArgumentException exception)
        {
            throw new BenchmarkSnapshotException("The frozen llama.cpp runtime configuration is invalid.")
            {
                Source = exception.Source
            };
        }

        if (runtime.ContextTokens < minimumContextTokens)
        {
            throw new BenchmarkSnapshotException("The frozen llama.cpp runtime context is smaller than the benchmark requirement.");
        }

        if (!runtime.LaunchPolicy.IsSupported)
        {
            throw new BenchmarkSnapshotException("The frozen llama.cpp benchmark launch policy is unsupported.");
        }
    }

    private static void ValidateSampling(BenchmarkSamplingSnapshotV1 sampling)
    {
        if (!string.Equals(sampling.SeedPolicy, BenchmarkFrozenPolicies.FixedSeedPolicy, StringComparison.Ordinal)
            || sampling.Stop is null
            || !SeedValue.TryParse(sampling.SeedValue, out _, out _))
        {
            throw new BenchmarkSnapshotException("The frozen benchmark sampling seed policy is unsupported.");
        }
    }

    private static void ValidateModel(BenchmarkInstalledModelSnapshotV1 model)
    {
        if (!string.Equals(model.ProviderName, "llamacpp", StringComparison.OrdinalIgnoreCase)
            || !IsV1Hash(model.RegistryRevision)
            || !IsV1Hash(model.RegistryAliasSetHash)
            || !IsV1Hash(model.PhysicalMemberSetHash)
            || !IsV1Hash(model.ModelContentFingerprint)
            || model.Members.Count == 0)
        {
            throw new BenchmarkSnapshotException("Installed model snapshot is incomplete or unsupported.");
        }
    }

    private static bool IsV1Hash(string value) =>
        value.Length == 67
        && value.StartsWith("v1:", StringComparison.Ordinal)
        && value.AsSpan(3).IndexOfAnyExcept(LowerHexCharacters) < 0;

    private static string ComputeHash(BenchmarkRuntimeSnapshotV1 snapshot)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(payload))}";
    }
}

public sealed class BenchmarkSnapshotException(string message) : InvalidOperationException(message);
