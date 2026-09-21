namespace XE_Local_AI_Engine.Providers.Ollama.Implementation;

using System.Net;
using OllamaSharp;
using OllamaSharp.Models;

/// <summary>
///     Evicts a loaded model from Ollama's memory by posting to <c>/api/generate</c> with <c>keep_alive=0</c> and the
///     <em>target</em> model name, so the eviction wire shape lives in exactly one place for both eject surfaces.
/// </summary>
/// <remarks>
///     OllamaSharp's <c>RequestModelUnloadAsync</c> extension builds its request with
///     <c>Model = client.SelectedModel</c>, ignoring the requested model entirely, so against a shared client it
///     unloads the wrong model and the one the operator asked to eject keeps its <c>expires_at</c>. Setting
///     <see cref="GenerateRequest.Model" /> and <see cref="GenerateRequest.KeepAlive" /> explicitly produces the body
///     Ollama answers with <c>done_reason="unload"</c>, evicting immediately.
/// </remarks>
public static class OllamaModelUnloader
{
    /// <summary>Requests immediate eviction of <paramref name="modelName" /> from Ollama's memory.</summary>
    /// <remarks>
    ///     Unloading a model the runtime is not holding is a harmless no-op, and a model it does not know answers 404,
    ///     which is absorbed here, so the eject action is idempotent for both callers rather than only for the loaded
    ///     case. Every other transport or runtime failure propagates.
    /// </remarks>
    /// <param name="client">The Ollama API client to send the unload request through.</param>
    /// <param name="modelName">The model to evict. This is sent verbatim as the request's <c>model</c> field.</param>
    /// <param name="cancellationToken">The token to cancel the operation with.</param>
    public static async Task UnloadAsync(IOllamaApiClient client, string modelName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var request = new GenerateRequest
        {
            Model = modelName,
            KeepAlive = "0",
            Stream = false
        };

        try
        {
            // Fully enumerate the single non-stream response so the request is dispatched: GenerateAsync sends no HTTP
            // call until drained. The chunks are unused; the expiry-to-zero side effect is all the eject action needs.
            await foreach (var chunk in client.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                _ = chunk;
            }
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // A model this runtime never HEARD of answers 404 and is already in the requested state, so absorb it here
            // rather than per call site. Match on the STATUS: only HTTP 400 becomes an OllamaException, never a 404.
        }
    }
}
