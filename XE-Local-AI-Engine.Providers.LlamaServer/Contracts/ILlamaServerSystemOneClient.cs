namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Asks a native decision model one question through llama-server's <c>POST /v1/systemone</c>.</summary>
public interface ILlamaServerSystemOneClient
{
    /// <summary>Answers the request, starting the decision process when it is cold; never throws for a server-side failure.</summary>
    /// <remarks>Only caller cancellation leaves as an exception.</remarks>
    Task<LlamaServerSystemOneResult> DecideAsync(LlamaServerSystemOneRequest request, CancellationToken cancellationToken);
}
