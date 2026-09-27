namespace XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Models;

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
