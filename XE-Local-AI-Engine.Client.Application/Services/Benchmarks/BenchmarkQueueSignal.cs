namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

public interface IBenchmarkQueueSignal
{
    void Wake();
    Task WaitAsync(TimeSpan pollInterval, CancellationToken cancellationToken);
}

public sealed class BenchmarkQueueSignal : IBenchmarkQueueSignal, IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Wake()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pending wake is sufficient; coalescing avoids unbounded producer pressure.
        }
    }

    public async Task WaitAsync(TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        _ = await _signal.WaitAsync(pollInterval, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() =>
        _signal.Dispose();
}
