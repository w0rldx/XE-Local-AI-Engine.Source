namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

internal sealed class LlamaServerEndpointBinding : ILlamaServerEndpointBinding
{
    private readonly AsyncLocal<BindingState?> _current = new();

    public IDisposable Bind(LlamaServerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var prior = _current.Value;
        var state = new BindingState { Endpoint = endpoint, Prior = prior };
        _current.Value = state;
        return new Scope(this, state);
    }

    public LlamaServerEndpoint? GetBoundEndpoint(string modelName, ModelRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        var current = _current.Value;
        return current is not null
               && current.Endpoint.Role == role
               && string.Equals(current.Endpoint.ModelName, modelName, StringComparison.Ordinal)
            ? current.Endpoint
            : null;
    }

    private sealed record BindingState
    {
        public required LlamaServerEndpoint Endpoint { get; init; }

        public required BindingState? Prior { get; init; }
    }

    private sealed class Scope : IDisposable
    {
        private readonly LlamaServerEndpointBinding _owner;
        private readonly BindingState _state;
        private int _disposed;

        public Scope(LlamaServerEndpointBinding owner, BindingState state)
        {
            _owner = owner;
            _state = state;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && ReferenceEquals(_owner._current.Value, _state))
            {
                _owner._current.Value = _state.Prior;
            }
        }
    }
}
