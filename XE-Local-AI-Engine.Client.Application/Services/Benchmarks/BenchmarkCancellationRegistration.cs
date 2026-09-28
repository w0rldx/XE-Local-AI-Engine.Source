namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class BenchmarkCancellationRegistration : IDisposable
{
    private readonly Action _dispose;
    private CancellationTokenSource? _source;

    internal BenchmarkCancellationRegistration(CancellationTokenSource source, Action dispose)
    {
        _source = source;
        _dispose = dispose;
    }

    public CancellationToken Token => _source?.Token ?? CancellationToken.None;

    public void Dispose()
    {
        var source = Interlocked.Exchange(ref _source, null);
        if (source is null)
        {
            return;
        }

        _dispose();
        source.Dispose();
    }
}
