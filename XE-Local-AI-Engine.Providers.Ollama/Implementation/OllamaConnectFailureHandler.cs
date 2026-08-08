namespace XE_Local_AI_Engine.Providers.Ollama.Implementation;

/// <summary>
///     Normalizes a connect-phase failure on the Ollama client to an <see cref="HttpRequestException" />. The short
///     <see cref="System.Net.Http.SocketsHttpHandler.ConnectTimeout" /> the Ollama client uses surfaces, when it fires,
///     as an <see cref="OperationCanceledException" /> (a <see cref="TaskCanceledException" /> whose inner exception is a
///     <see cref="TimeoutException" />) — NOT the <see cref="HttpRequestException" /> the rest of the codebase catches to
///     detect an unreachable daemon. A host that refuses the connection instantly (TCP RST) already throws
///     <see cref="HttpRequestException" />; a host that silently drops the SYN (for example an absent IPv6 <c>::1</c>
///     behind a default firewall, the common Windows case for <c>localhost</c>) makes the connect hang until the timeout
///     and throws the cancellation form instead. Translating it here makes "Ollama unreachable" present identically in
///     both cases, so the existing graceful-degradation handling (model list, model-details, chat capability detection)
///     applies the same way everywhere.
/// </summary>
/// <remarks>
///     A genuine caller cancellation (the supplied <see cref="CancellationToken" /> is signalled) is never translated and
///     propagates unchanged, so user-aborted requests keep their cancellation semantics.
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
            // The caller did not cancel, so this is the connect/request timeout firing against an absent or
            // unreachable daemon, not a real cancellation. Present it as a connection failure so callers that already
            // treat HttpRequestException as "Ollama is offline" degrade gracefully instead of letting the cancellation
            // propagate and tear down the request (for example a chat send).
            throw new HttpRequestException("The Ollama endpoint could not be reached within the connection timeout.", exception);
        }
    }
}
