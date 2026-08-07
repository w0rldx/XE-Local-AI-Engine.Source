namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

using System.Collections.Concurrent;

/// <summary>Process-local cancellation handles keyed by the durable claim identity.</summary>
internal sealed class McpAgentRunCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private int _stopping;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "A successful dictionary registration transfers CancellationTokenSource ownership to Remove; failed registration disposes it here.")]
    public McpAgentRunRegistrationKind TryRegister(Guid requestId, Guid claimToken, long version, out CancellationToken token)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            token = default;
            return McpAgentRunRegistrationKind.ShuttingDown;
        }

        var entry = new Entry(claimToken, version, new CancellationTokenSource());
        if (_entries.TryAdd(requestId, entry))
        {
            token = entry.Source.Token;
            return Volatile.Read(ref _stopping) == 0
                ? McpAgentRunRegistrationKind.Registered
                : McpAgentRunRegistrationKind.ShuttingDown;
        }

        entry.Source.Dispose();
        token = default;
        return McpAgentRunRegistrationKind.Duplicate;
    }

    public bool Signal(Guid requestId, Guid? claimToken)
    {
        if (!_entries.TryGetValue(requestId, out var entry)
            || (claimToken is not null && entry.ClaimToken != claimToken))
        {
            return false;
        }

        return entry.TryCancel();
    }

    public IReadOnlyList<McpAgentRunCancellationHandle> BeginShutdown()
    {
        Interlocked.Exchange(ref _stopping, 1);
        return Snapshot();
    }

    private IReadOnlyList<McpAgentRunCancellationHandle> Snapshot() =>
        _entries.Select(static pair => new McpAgentRunCancellationHandle(pair.Key,
                pair.Value.ClaimToken,
                pair.Value.Version))
                .ToArray();

    public void Remove(Guid requestId, Guid claimToken)
    {
        if (_entries.TryGetValue(requestId, out var entry)
            && entry.ClaimToken == claimToken
            && _entries.TryRemove(requestId, out var removed))
        {
            removed.Source.Dispose();
        }
    }

    private sealed record Entry(Guid ClaimToken, long Version, CancellationTokenSource Source)
    {
        public bool TryCancel()
        {
            try
            {
                Source.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (AggregateException)
            {
                // A consumer cancellation callback failed after the durable marker had already committed. Cancellation
                // was still requested for every callback; lifecycle finalization remains based on the persisted marker.
                return true;
            }
        }
    }
}

internal sealed record McpAgentRunCancellationHandle(Guid RequestId, Guid ClaimToken, long Version);

internal enum McpAgentRunRegistrationKind
{
    Registered,
    ShuttingDown,
    Duplicate
}
