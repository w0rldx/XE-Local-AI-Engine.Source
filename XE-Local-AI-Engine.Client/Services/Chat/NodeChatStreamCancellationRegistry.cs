namespace XE_Local_AI_Engine.Client.Services.Chat;

using System.Collections.Concurrent;

public sealed class NodeChatStreamCancellationRegistry : INodeChatStreamCancellationRegistry
{
    private readonly ConcurrentDictionary<NodeChatMessageCorrelation, Action> _activeStreams = [];

    public IDisposable Register(NodeChatMessageCorrelation correlation, Action cancel)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(cancel);

        if (!_activeStreams.TryAdd(correlation, cancel))
        {
            throw new InvalidOperationException("A node chat stream with the same correlation is already active.");
        }

        return new Registration(_activeStreams, correlation);
    }

    public bool TryCancel(NodeChatMessageCorrelation correlation)
    {
        ArgumentNullException.ThrowIfNull(correlation);

        if (!_activeStreams.TryGetValue(correlation, out var cancel))
        {
            return false;
        }

        cancel();
        return true;
    }

    private sealed class Registration(
        ConcurrentDictionary<NodeChatMessageCorrelation, Action> activeStreams,
        NodeChatMessageCorrelation correlation) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _ = activeStreams.TryRemove(correlation, out _);
        }
    }
}
