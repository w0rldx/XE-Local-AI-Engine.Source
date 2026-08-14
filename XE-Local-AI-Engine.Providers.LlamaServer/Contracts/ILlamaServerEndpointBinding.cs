namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Binds one async invocation flow to an already-running supervised endpoint.</summary>
public interface ILlamaServerEndpointBinding
{
    IDisposable Bind(LlamaServerEndpoint endpoint);
    LlamaServerEndpoint? GetBoundEndpoint(string modelName, ModelRole role);
}
