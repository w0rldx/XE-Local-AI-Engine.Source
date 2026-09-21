namespace XE_Local_AI_Engine.Providers.Ollama.Implementation;

using OllamaSharp;

/// <summary>
///     Owns the single hardened <see cref="HttpClient" /> — short <see cref="SocketsHttpHandler.ConnectTimeout" /> plus
///     <see cref="OllamaConnectFailureHandler" /> normalization — and mints every Ollama client over it.
/// </summary>
/// <remarks>
///     The singleton management client and each per-model client resolve through the SAME transport, so a routed send
///     gets the identical fail-fast connect bound and "Ollama unreachable" normalization, instead of the raw default
///     transport a bare <c>new OllamaApiClient(uri, model)</c> allocates per model. It also avoids per-model handler
///     churn: <see cref="OllamaApiClient" />'s HttpClient-accepting constructor leaves <c>_disposeHttpClient</c>
///     false, so disposing a per-model client never tears down the shared transport, which this singleton owns.
/// </remarks>
public sealed class OllamaApiClientFactory : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    /// <param name="httpClient">The hardened client (its <see cref="HttpClient.BaseAddress" /> pins the Ollama endpoint).</param>
    /// <param name="ownsHttpClient">
    ///     When <see langword="true" /> this factory disposes <paramref name="httpClient" /> on <see cref="Dispose" />.
    ///     The production singleton owns it; a test supplying its own client can opt out.
    /// </param>
    public OllamaApiClientFactory(HttpClient httpClient, bool ownsHttpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
    }

    /// <summary>
    ///     Creates an Ollama client bound to <paramref name="selectedModel" /> over the shared hardened transport. The
    ///     returned client is safe for the caller to dispose: it does not own the shared <see cref="HttpClient" />.
    /// </summary>
    public OllamaApiClient CreateClient(string? selectedModel)
    {
        return new OllamaApiClient(_httpClient)
        {
            SelectedModel = selectedModel ?? string.Empty
        };
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
