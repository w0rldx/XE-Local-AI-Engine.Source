namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Collections.Concurrent;

/// <summary>
///     Represents node chat stream cancellation registry.
/// </summary>
public sealed class NodeChatStreamCancellationRegistry : INodeChatStreamCancellationRegistry
{
    private readonly ConcurrentDictionary<NodeChatMessageCorrelation, Registration> _activeStreams = [];

    public IDisposable Register(NodeChatMessageCorrelation correlation, Action cancel)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(cancel);

        var registration = new Registration(_activeStreams, correlation, cancel);
        if (!_activeStreams.TryAdd(correlation, registration))
        {
            throw new NodeChatStreamAlreadyActiveException();
        }

        return registration;
    }

    public bool TryCancel(NodeChatMessageCorrelation correlation)
    {
        ArgumentNullException.ThrowIfNull(correlation);

        if (!_activeStreams.TryGetValue(correlation, out var registration))
        {
            return false;
        }

        return registration.TryCancel();
    }

    private sealed class Registration(
        ConcurrentDictionary<NodeChatMessageCorrelation, Registration> activeStreams,
        NodeChatMessageCorrelation correlation,
        Action cancel) : IDisposable
    {
        private readonly Lock _gate = new();
        private bool _disposed;

        public bool TryCancel()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                cancel();
                return true;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _ = activeStreams.TryRemove(correlation, out _);
            }
        }
    }
}
