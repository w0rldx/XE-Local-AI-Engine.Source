namespace XE_Local_AI_Engine.Providers.Ollama.Implementation;

/// <summary>
///     Normalizes a connect-phase failure on the Ollama client to an <see cref="HttpRequestException" />, the shape the
///     rest of the codebase catches to detect an unreachable daemon.
/// </summary>
/// <remarks>
///     A fired <see cref="System.Net.Http.SocketsHttpHandler.ConnectTimeout" /> surfaces as an
///     <see cref="OperationCanceledException" /> instead. A host that refuses instantly with a TCP RST already throws
///     <see cref="HttpRequestException" />, while one that silently drops the SYN — an absent IPv6 <c>::1</c> behind a
///     default firewall, the common Windows <c>localhost</c> case — hangs until the timeout and throws the cancellation
///     form. A genuine caller cancellation is never translated and propagates unchanged.
/// </remarks>
internal sealed class OllamaConnectFailureHandler : DelegatingHandler
{
    public OllamaConnectFailureHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller did not cancel, so this is the timeout firing against an absent daemon. Present it as a
            // connection failure, so callers treating HttpRequestException as "Ollama is offline" degrade gracefully.
            throw new HttpRequestException("The Ollama endpoint could not be reached within the connection timeout.", exception);
        }
    }
}
