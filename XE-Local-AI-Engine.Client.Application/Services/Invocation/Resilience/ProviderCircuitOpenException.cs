namespace XE_Local_AI_Engine.Client.Services.Invocation.Resilience;

/// <summary>
///     Raised when a provider endpoint's circuit breaker is open and a send is rejected fast. The message is a fixed,
///     path-free constant; the endpoint identifier is kept internal for server-side logging only.
/// </summary>
public sealed class ProviderCircuitOpenException : Exception
{
    public ProviderCircuitOpenException(string endpointKey)
        : base("Provider temporarily unavailable.")
    {
        EndpointKey = endpointKey;
    }

    public string EndpointKey { get; }
}
