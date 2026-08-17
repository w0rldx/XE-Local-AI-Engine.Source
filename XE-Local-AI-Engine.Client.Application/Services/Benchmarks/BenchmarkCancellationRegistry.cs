namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Entities;

public interface IBenchmarkCancellationRegistry
{
    BenchmarkCancellationRegistration Register(Guid runId, BenchmarkWorkKind kind, CancellationToken hostToken);
    bool TryCancel(Guid runId, BenchmarkWorkKind kind);
}

public enum BenchmarkCancellationTarget
{
    Primary,
    Judge
}

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

public sealed class BenchmarkCancellationRegistry : IBenchmarkCancellationRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<RegistrationKey, CancellationTokenSource> _registrations = [];

    public BenchmarkCancellationRegistration Register(Guid runId, BenchmarkWorkKind kind, CancellationToken hostToken)
    {
        var key = new RegistrationKey(runId, kind);
        var source = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        lock (_gate)
        {
            if (!_registrations.TryAdd(key, source))
            {
                source.Dispose();
                throw new InvalidOperationException("A benchmark work item already owns cancellation for this run and kind.");
            }
        }

        return new BenchmarkCancellationRegistration(source, () => Remove(key, source));
    }

    public bool TryCancel(Guid runId, BenchmarkWorkKind kind)
    {
        CancellationTokenSource? source;
        lock (_gate)
        {
            _registrations.TryGetValue(new RegistrationKey(runId, kind), out source);
        }

        if (source is null)
        {
            return false;
        }

        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void Remove(RegistrationKey key, CancellationTokenSource source)
    {
        lock (_gate)
        {
            if (_registrations.TryGetValue(key, out var current) && ReferenceEquals(current, source))
            {
                _registrations.Remove(key);
            }
        }
    }

    private sealed record RegistrationKey(Guid RunId, BenchmarkWorkKind Kind);
}
