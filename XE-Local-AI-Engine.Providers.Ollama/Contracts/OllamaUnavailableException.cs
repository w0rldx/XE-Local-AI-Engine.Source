namespace XE_Local_AI_Engine.Providers.Ollama.Contracts;

/// <summary>
///     Thrown when the Ollama daemon could not be reached or rejected a request. It is the provider-owned replacement
///     for the SDK's transport exception, so consumers never catch an OllamaSharp type and the SDK stays inside
///     <c>Providers.Ollama</c>.
/// </summary>
/// <remarks>
///     The <see cref="Exception.Message" /> is safe to surface; the original transport failure is kept only as
///     <see cref="Exception.InnerException" /> for diagnostics.
/// </remarks>
public sealed class OllamaUnavailableException : Exception
{
    /// <summary>Creates the failure with a sanitized message wrapping the original transport cause.</summary>
    public OllamaUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
