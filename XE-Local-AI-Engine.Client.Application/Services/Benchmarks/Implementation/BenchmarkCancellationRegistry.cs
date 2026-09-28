namespace XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Entities;

public sealed class BenchmarkCancellationRegistry : IBenchmarkCancellationRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<RegistrationKey, CancellationTokenSource> _registrations = [];

    public BenchmarkCancellationRegistration Register(Guid runId, BenchmarkWorkKind kind, CancellationToken hostToken)
    {
        var key = new RegistrationKey
        {
            RunId = runId,
            Kind = kind
        };
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
            _registrations.TryGetValue(new RegistrationKey
            {
                RunId = runId,
                Kind = kind
            }, out source);
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

    private sealed record RegistrationKey
    {
        public required Guid RunId { get; init; }

        public required BenchmarkWorkKind Kind { get; init; }
    }
}
