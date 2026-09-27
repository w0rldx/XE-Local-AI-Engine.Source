namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Deterministic canonical form of a judge policy. Two policies that differ only in the order the operator entered the
/// criteria hash identically; any field change does not.
/// </summary>
public static class BenchmarkJudgePolicyCanonicalizer
{
    private static readonly JsonSerializerOptions CanonicalOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    public static string ToCanonicalJson(BenchmarkJudgePolicyV1 policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Validate(policy);
        return JsonSerializer.Serialize(Normalize(policy), CanonicalOptions);
    }

    private static void Validate(BenchmarkJudgePolicyV1 policy)
    {
        if (policy.Model?.MemberHashes is null || policy.Rubric?.Criteria is null)
        {
            throw new ArgumentException("A judge policy must carry a model identity and a rubric to be canonicalised.", nameof(policy));
        }
    }

    public static string ComputePolicyHash(BenchmarkJudgePolicyV1 policy) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToCanonicalJson(policy))));

    private static BenchmarkJudgePolicyV1 Normalize(BenchmarkJudgePolicyV1 policy)
    {
        return policy with
        {
            Model = policy.Model with
            {
                MemberHashes = [.. policy.Model.MemberHashes.Order(StringComparer.Ordinal)]
            },
            Mode = BenchmarkJudgePolicyModes.Normalize(policy.Mode),
            Rubric = policy.Rubric with
            {
                Criteria =
                [
                    .. policy.Rubric.Criteria.OrderBy(static criterion => criterion.Id, StringComparer.Ordinal)
                             .Select(static criterion => criterion with
                             {
                                 Kind = BenchmarkJudgeCriterionKinds.Normalize(criterion.Kind),
                                 Config = BenchmarkJudgeVerifierConfig.Canonicalize(criterion.Config)
                             })
                ]
            }
        };
    }
}
