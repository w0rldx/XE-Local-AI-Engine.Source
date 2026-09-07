namespace XE_Local_AI_Engine.Providers.Ollama.Implementation;

using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Exceptions;

/// <summary>
///     Evicts a loaded model from Ollama's memory by posting to <c>/api/generate</c> with <c>keep_alive=0</c> and the
///     <em>target</em> model name. Shared by the two surfaces that expose the eject action — the app-service
///     <c>OllamaModelService</c> (consumed by the <c>models/{modelName}/unload</c> endpoint) and the provider-neutral
///     <c>OllamaLocalModelProvider</c> — so the eviction wire shape lives in exactly one place.
/// </summary>
/// <remarks>
///     OllamaSharp's <c>RequestModelUnloadAsync</c> extension builds its request with
///     <c>Model = client.SelectedModel</c>, ignoring the requested model entirely. Against a shared client whose
///     <c>SelectedModel</c> is a fixed configured model (or empty), the unload targets the wrong model, so the model the
///     operator asked to eject keeps its <c>expires_at</c> and is never freed. Setting <see cref="GenerateRequest.Model" />
///     to the requested name and <see cref="GenerateRequest.KeepAlive" /> to <c>"0"</c> produces
///     <c>{"model":"&lt;name&gt;","keep_alive":"0","stream":false}</c>, which Ollama answers with
///     <c>done_reason="unload"</c> and evicts immediately.
/// </remarks>
public static class OllamaModelUnloader
{
    /// <summary>
    ///     Requests immediate eviction of <paramref name="modelName" /> from Ollama's memory. Unloading a model the
    ///     runtime is not currently holding is a harmless no-op, and a model it does not know at all is swallowed here
    ///     (see the remarks), so the eject action is idempotent for both callers rather than only for the loaded case.
    /// </summary>
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
            // Fully enumerate the (single, non-stream) response so the request is actually dispatched. GenerateAsync is a
            // streaming method; without draining the enumerator no HTTP call is sent. The chunks themselves are unused — the
            // side effect (Ollama setting the model's expiry to zero) is all the eject action needs.
            await foreach (var chunk in client.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                _ = chunk;
            }
        }
        catch (OllamaException exception) when (exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            // A model this runtime does not hold is a no-op, but a model it has never HEARD of is a 404 whose body reads
            // "model '<name>' not found, try pulling it first" — which OllamaSharp raises as an OllamaException carrying
            // that text. Both callers document eject as idempotent, and a model Ollama does not know is already in the
            // requested state, so the not-found case is absorbed here (the one place the eviction wire shape lives)
            // rather than at each call site. Any other OllamaException still propagates.
        }
    }
}
