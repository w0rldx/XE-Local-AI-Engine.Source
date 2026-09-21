namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>Reads and decrypts a project's current judge policy for the wire.</summary>
internal static class BenchmarkJudgePolicyProjection
{
    public static async Task<BenchmarkJudgePolicyResponse> ReadAsync(BenchmarkRecordService records, Guid projectId, CancellationToken ct)
    {
        var revision = await records.GetCurrentJudgePolicyRevisionAsync(projectId, ct);
        var policy = revision?.PolicyJson is { } payload && !payload.IsEmpty
            ? BenchmarkJudgeSerialization.DeserializePolicy(payload.Span)
            : null;
        return BenchmarkEndpointMapper.ToJudgePolicy(revision, policy);
    }
}
