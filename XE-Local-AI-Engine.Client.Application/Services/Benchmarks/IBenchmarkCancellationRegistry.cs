namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Entities;

public interface IBenchmarkCancellationRegistry
{
    BenchmarkCancellationRegistration Register(Guid runId, BenchmarkWorkKind kind, CancellationToken hostToken);
    bool TryCancel(Guid runId, BenchmarkWorkKind kind);
}
