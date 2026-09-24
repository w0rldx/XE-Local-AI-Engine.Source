namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>The immutable measurement history behind a run's displayed numbers.</summary>
public sealed class ListBenchmarkFidelityAttemptsEndpoint : Endpoint<ListBenchmarkFidelityAttemptsRequest, ListBenchmarkFidelityAttemptsResponse>
{
    private readonly BenchmarkRecordService _records;

    public ListBenchmarkFidelityAttemptsEndpoint(BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.RunFidelityAttempts);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ListBenchmarkFidelityAttemptsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (await _records.GetRunAsync(req.RunId, ct) is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark run was not found.")));
            return;
        }

        var attempts = await _records.ListFidelityAttemptsAsync(req.RunId, ct);
        await Send.OkAsync(new ListBenchmarkFidelityAttemptsResponse
        {
            Items =
            [
                .. attempts.Select(static attempt => new BenchmarkFidelityAttemptResponse
                {
                    Id = attempt.Id,
                    Sequence = attempt.Sequence,
                    Kind = attempt.Kind,
                    Status = attempt.Status.ToString(),
                    PerplexityMean = attempt.PerplexityMean,
                    PerplexityStdErr = attempt.PerplexityStdErr,
                    PerplexityChunks = attempt.PerplexityChunks,
                    PerplexityContextTokens = attempt.PerplexityContextTokens,
                    CorpusId = attempt.CorpusId,
                    KldMean = attempt.KldMean,
                    KldP99 = attempt.KldP99,
                    TopTokenAgreement = attempt.TopTokenAgreement,
                    BaseModelName = attempt.BaseModelName,
                    BaseModelContentFingerprint = attempt.BaseModelContentFingerprint,
                    BaseLogitsDigest = attempt.BaseLogitsDigest,
                    ErrorMessage = attempt.ErrorMessage,
                    EnqueuedAtUtc = attempt.EnqueuedAtUtc,
                    StartedAtUtc = attempt.StartedAtUtc,
                    CompletedAtUtc = attempt.CompletedAtUtc
                })
            ]
        }, ct);
    }
}
