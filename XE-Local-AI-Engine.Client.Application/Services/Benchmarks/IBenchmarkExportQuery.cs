namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

public interface IBenchmarkExportQuery
{
    Task<BenchmarkJsonExportQueryResult?> GetJsonAsync(Guid projectId, CancellationToken ct);

    Task<BenchmarkCsvExportQueryResult?> GetCsvAsync(Guid projectId, CancellationToken ct);
}
