namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>The at-rest form of the judge policy and the per-attempt judge runtime.</summary>
/// <remarks>
///     The policy is stored in its CANONICAL form, so the stored bytes re-hash to the stored <c>PolicyHash</c> and a
///     revision can be verified without a second serializer agreeing with the first.
/// </remarks>
public static class BenchmarkJudgeSerialization
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false
    };

    public static byte[] SerializePolicy(BenchmarkJudgePolicyV1 policy) =>
        Encoding.UTF8.GetBytes(BenchmarkJudgePolicyCanonicalizer.ToCanonicalJson(policy));

    /// <summary>The stored policy, validated STRUCTURALLY only.</summary>
    /// <remarks>
    ///     A version constant moving must never make an already-stored revision unreadable: with the strict validator
    ///     here, bumping <see cref="BenchmarkJudgePolicyVersions.PromptVersion" /> made `GET benchmarks/projects/{id}`
    ///     and the project export throw, the project header disappeared from the UI, and it took the re-save control
    ///     that heals the revision with it. Write and execution re-validate with <c>strictVersions: true</c>.
    /// </remarks>
    public static BenchmarkJudgePolicyV1 DeserializePolicy(ReadOnlySpan<byte> payload)
    {
        try
        {
            var policy = JsonSerializer.Deserialize<BenchmarkJudgePolicyV1>(payload, Options)
                         ?? throw new BenchmarkSnapshotException("The stored judge policy is empty.");
            BenchmarkJudgePolicyValidator.Validate(policy, strictVersions: false);
            return policy;
        }
        catch (JsonException exception)
        {
            throw new BenchmarkSnapshotException("The stored judge policy is invalid.", exception)
            {
                Source = exception.Source
            };
        }
    }

    /// <summary>The revision's stored policy, or null when there is no revision or it holds no policy bytes.</summary>
    public static BenchmarkJudgePolicyV1? DeserializeRevisionPolicy(BenchmarkJudgePolicyRevisionRecord? revision) =>
        revision?.PolicyJson is { } payload && !payload.IsEmpty
            ? DeserializePolicy(payload.Span)
            : null;

    public static byte[] SerializeResult(BenchmarkJudgeResultV2 result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, Options);

    /// <summary>The stored verdict, or <see langword="null" /> when the payload is absent or unreadable.</summary>
    /// <remarks>
    ///     Reads through the WRITER's own options: these are camelCase, so a reader that re-derives default options
    ///     binds every property to its default and hands the API a zeroed verdict instead of failing.
    /// </remarks>
    public static BenchmarkJudgeResultV2? DeserializeResult(ReadOnlyMemory<byte>? payload)
    {
        if (payload is not { } value || value.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BenchmarkJudgeResultV2>(value.Span, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static byte[] SerializeRuntime(BenchmarkJudgeRuntimeV1 runtime) =>
        JsonSerializer.SerializeToUtf8Bytes(runtime, Options);

    public static BenchmarkJudgeRuntimeV1 DeserializeRuntime(ReadOnlySpan<byte> payload)
    {
        try
        {
            var runtime = JsonSerializer.Deserialize<BenchmarkJudgeRuntimeV1>(payload, Options)
                          ?? throw new BenchmarkSnapshotException("The frozen judge runtime is empty.");
            if (runtime.SchemaVersion != BenchmarkJudgeRuntimeV1.CurrentSchemaVersion
                || runtime.Model is null
                || runtime.Runtime is null
                || runtime.Sampling is null
                || runtime.RequestedContextTokens <= 0)
            {
                throw new BenchmarkSnapshotException("The frozen judge runtime is unsupported.");
            }

            return runtime;
        }
        catch (JsonException exception)
        {
            throw new BenchmarkSnapshotException("The frozen judge runtime is invalid.", exception)
            {
                Source = exception.Source
            };
        }
    }
}
