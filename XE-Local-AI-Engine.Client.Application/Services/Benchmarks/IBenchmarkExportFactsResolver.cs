namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Stores;

public interface IBenchmarkExportFactsResolver
{
    BenchmarkFidelityDisplayFacts ResolveProject(BenchmarkProjectRecord project);

    BenchmarkExportRunFacts ResolveRun(BenchmarkRunRecord run);
}
