namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using Microsoft.Net.Http.Headers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

/// <summary>Downloads one run artifact's exact bytes as a plain-text attachment.</summary>
/// <remarks>
///     The content is candidate-controlled, so it is only ever an attachment with <c>nosniff</c>, never rendered. Bytes that fail decryption or no
///     longer match the row's hash or size are refused with 409 <c>ArtifactCorrupt</c> and never served.
/// </remarks>
public sealed class GetBenchmarkRunArtifactEndpoint : Endpoint<BenchmarkRunArtifactRouteRequest>
{
    private readonly BenchmarkRunArtifactBlobStore _blobs;
    private readonly ILogger<GetBenchmarkRunArtifactEndpoint> _logger;
    private readonly BenchmarkRecordService _records;

    public GetBenchmarkRunArtifactEndpoint(BenchmarkRecordService records, BenchmarkRunArtifactBlobStore blobs, ILogger<GetBenchmarkRunArtifactEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(logger);
        _records = records;
        _blobs = blobs;
        _logger = logger;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.RunArtifactById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<string>(StatusCodes.Status200OK, "text/plain")
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(BenchmarkRunArtifactRouteRequest req, CancellationToken ct)
    {
        var artifacts = await _records.GetRunArtifactsAsync(req.RunId, ct);
        if (artifacts.FirstOrDefault(artifact => artifact.Id == req.ArtifactId) is not { } artifact)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark run artifact was not found.")));
            return;
        }

        var read = await _blobs.ReadAsync(artifact.RunId, artifact.Id, artifact.ContentHash, artifact.ByteCount, ct);
        switch (read.Status)
        {
            case BenchmarkRunArtifactBlobReadStatus.Found:
                HttpContext.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
                await Send.BytesAsync(read.Content.ToArray(), $"{artifact.Kind}-{artifact.Id:N}.txt", artifact.MediaType, cancellation: ct);
                return;
            case BenchmarkRunArtifactBlobReadStatus.Missing:
                await Send.ResultAsync(BenchmarkEndpointSupport.Problem(StatusCodes.Status404NotFound, BenchmarkErrorCode.ArtifactMissing,
                    "The artifact's content is no longer on disk."));
                return;
            default:
                _logger.LogWarning("Benchmark run {RunId} artifact {ArtifactId} failed its integrity check ({Status}) and was not served.",
                    artifact.RunId, artifact.Id, read.Status);
                throw new BenchmarkConflictException("ArtifactCorrupt");
        }
    }
}
