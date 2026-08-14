namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

internal sealed class LlamaServerEndpointBinding : ILlamaServerEndpointBinding
{
    private readonly AsyncLocal<BindingState?> _current = new();

    public IDisposable Bind(LlamaServerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var prior = _current.Value;
        var state = new BindingState(endpoint, prior);
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

    private sealed record BindingState(LlamaServerEndpoint Endpoint, BindingState? Prior);

    private sealed class Scope(LlamaServerEndpointBinding owner, BindingState state) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && ReferenceEquals(owner._current.Value, state))
            {
                owner._current.Value = state.Prior;
            }
        }
    }
}
