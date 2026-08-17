namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Models;

public interface IBenchmarkInstalledModelLease : IAsyncDisposable
{
    InstalledModelSnapshot Snapshot { get; }
}
