namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using System.Text;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>Downloads a project's flat benchmark run projection as RFC 4180 CSV.</summary>
public sealed class ExportBenchmarkProjectCsvEndpoint : Endpoint<BenchmarkProjectRouteRequest>
{
    private readonly IBenchmarkExportQuery _exports;
    private readonly TimeProvider _timeProvider;

    public ExportBenchmarkProjectCsvEndpoint(IBenchmarkExportQuery exports, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(exports);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _exports = exports;
        _timeProvider = timeProvider;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.ProjectExportCsv);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<string>(StatusCodes.Status200OK, "text/csv")
                                      .ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(BenchmarkProjectRouteRequest req, CancellationToken ct)
    {
        var export = await _exports.GetCsvAsync(req.ProjectId, ct);
        if (export is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found.")));
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var csv = BenchmarkExportCsv.Render(export.Runs,
            export.Fidelity.ExpectedKldDigest,
            export.PairwiseFit);
        await Send.BytesAsync(Encoding.UTF8.GetBytes(csv),
            BenchmarkExportProjection.FileName(export.Project.Name, now, "csv"),
            "text/csv",
            cancellation: ct);
    }
}
