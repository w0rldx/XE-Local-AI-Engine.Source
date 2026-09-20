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

public static class BenchmarkFrozenPolicies
{
    public const string FixedSeedPolicy = "fixed";

    /// <summary>The frozen sampling every benchmark generation replays.</summary>
    /// <remarks>
    ///     <paramref name="maxOutputTokens" /> is the project's optional output budget (<c>n_predict</c>); the default
    ///     keeps generation context-limited and keeps the judge-policy sampling — which never takes a budget —
    ///     byte-identical to what it has always hashed. The same holds for the two reasoning-budget arguments:
    ///     omitted, they are omitted from the payload entirely.
    /// </remarks>
    public static BenchmarkSamplingSnapshotV1 DeterministicSampling(int? maxOutputTokens = null,
        int? reasoningBudgetTokens = null,
        bool? reasoningBudgetEnforceable = null) =>
        new(0, null, null, null, maxOutputTokens, null, null, null, null, [], FixedSeedPolicy, "0", reasoningBudgetTokens,
            reasoningBudgetEnforceable);

    /// <summary>
    ///     Tokens a project's context must keep clear of its own budgets, so a run that pins both a reasoning budget
    ///     and an output budget still has room for the task, the system prompt and the agent's tool offer.
    /// </summary>
    /// <remarks>
    ///     A coarse floor on purpose: the exact prompt is not known when the project is validated, and a floor that
    ///     refuses the obviously impossible is worth more than a precise one that needs the frozen runtime to compute.
    /// </remarks>
    public const int MinimumPromptReserveTokens = 512;

    /// <summary>The generation budget a run gets when its project does not pin one. See <see cref="FrozenTimeouts" />.</summary>
    public const int DefaultInvocationTimeoutSeconds = 900;

    /// <summary>The bounds an operator-chosen generation budget must sit inside.</summary>
    public const int MinInvocationTimeoutSeconds = 60;

    public const int MaxInvocationTimeoutSeconds = 7200;

    /// <summary>
    ///     The timeout policy a benchmark generation runs under, pinned here because the node-level
    ///     <see cref="TimeoutSettings.InvocationTimeoutSeconds" /> default has since moved.
    /// </summary>
    /// <remarks>
    ///     A frozen run replays identically across app versions instead of inheriting whatever the package builder
    ///     defaults to. Only the invocation budget is operator-tunable — the tool-call and stream-idle budgets bound a
    ///     STALL, not a legitimate answer's length. The default is 900 s, not 300: at 300 a 27B reasoning model was
    ///     cancelled mid-answer at 307 s, so the timeout measured the harness rather than the model. Timeout values
    ///     are NOT part of the versioned configuration hash, so changing a pinned one is not reflected there.
    /// </remarks>
    public static TimeoutSettings FrozenTimeouts(int? invocationTimeoutSeconds = null) =>
        new()
        {
            InvocationTimeoutSeconds = invocationTimeoutSeconds ?? DefaultInvocationTimeoutSeconds,
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

public sealed class BenchmarkRuntimeSnapshotFactory : IBenchmarkRuntimeSnapshotFactory
{
    private static readonly SearchValues<char> LowerHexCharacters = SearchValues.Create("0123456789abcdef");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false
    };

    private readonly IBenchmarkEligibilityPolicy _eligibilityPolicy;

    public BenchmarkRuntimeSnapshotFactory(IBenchmarkEligibilityPolicy eligibilityPolicy)
    {
        ArgumentNullException.ThrowIfNull(eligibilityPolicy);
        _eligibilityPolicy = eligibilityPolicy;
    }

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
            throw new BenchmarkSnapshotException("The frozen llama.cpp runtime configuration is invalid.", exception)
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
