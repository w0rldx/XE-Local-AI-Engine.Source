namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

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
    BenchmarkInstalledModelSnapshotV1 PrimaryModel,
    BenchmarkJudgeSnapshotV1 Judge,
    BenchmarkFreezeDependencySetV1 Dependencies,
    string ApplicationVersion,
    long CreatedAtUtc);

public sealed record BenchmarkRuntimeSnapshotV1(
    int SchemaVersion,
    Guid ProjectId,
    Guid AgentDefinitionId,
    long AgentVersion,
    string CoreTask,
    int RequestedContextTokens,
    ResolvedAgentRuntime ResolvedRuntime,
    BenchmarkInstalledModelSnapshotV1 PrimaryModel,
    BenchmarkJudgeSnapshotV1 Judge,
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

public sealed record BenchmarkJudgeSnapshotV1(
    bool Enabled,
    BenchmarkInstalledModelSnapshotV1? Model,
    int PromptVersion,
    int OutputSchemaVersion,
    int? RequestedContextTokens,
    string ConfigurationHash);

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
        ValidateJudge(input.Judge);
        var unhashed = new BenchmarkRuntimeSnapshotV1(1, input.ProjectId, input.AgentDefinitionId, input.AgentVersion,
            input.CoreTask, input.RequestedContextTokens, eligibleRuntime, input.PrimaryModel, input.Judge, input.Dependencies,
            input.ApplicationVersion, input.CreatedAtUtc, string.Empty);
        return unhashed with { ConfigurationHash = ComputeHash(unhashed) };
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
        ValidateModel(snapshot.PrimaryModel);
        ValidateJudge(snapshot.Judge);
        var expected = ComputeHash(snapshot with { ConfigurationHash = string.Empty });
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(snapshot.ConfigurationHash)))
        {
            throw new BenchmarkSnapshotException("Benchmark snapshot configuration hash is invalid.");
        }
    }

    private static void ValidateJudge(BenchmarkJudgeSnapshotV1 judge)
    {
        if (!judge.Enabled)
        {
            if (judge.Model is not null)
            {
                throw new BenchmarkSnapshotException("A disabled judge cannot carry a model snapshot.");
            }

            return;
        }

        if (judge.Model is null || judge.RequestedContextTokens is null or <= 0)
        {
            throw new BenchmarkSnapshotException("An enabled judge requires a verified model and context.");
        }

        ValidateModel(judge.Model);
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

    private static bool IsV1Hash(string value) => value.Length == 67
                                                   && value.StartsWith("v1:", StringComparison.Ordinal)
                                                   && value.AsSpan(3).IndexOfAnyExcept(LowerHexCharacters) < 0;

    private static string ComputeHash(BenchmarkRuntimeSnapshotV1 snapshot)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(payload))}";
    }
}

public sealed class BenchmarkSnapshotException(string message) : InvalidOperationException(message);
