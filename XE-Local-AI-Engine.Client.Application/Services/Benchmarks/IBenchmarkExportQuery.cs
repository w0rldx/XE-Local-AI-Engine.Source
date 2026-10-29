namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

public interface IBenchmarkExportQuery
{
    /// <exception cref="XE_Local_AI_Engine.Client.Persistence.Stores.BenchmarkNotFoundException">No project carries <paramref name="projectId" />.</exception>
    Task<BenchmarkJsonExportQueryResult> GetJsonAsync(Guid projectId, CancellationToken ct);

    /// <exception cref="XE_Local_AI_Engine.Client.Persistence.Stores.BenchmarkNotFoundException">No project carries <paramref name="projectId" />.</exception>
    Task<BenchmarkCsvExportQueryResult> GetCsvAsync(Guid projectId, CancellationToken ct);
}
