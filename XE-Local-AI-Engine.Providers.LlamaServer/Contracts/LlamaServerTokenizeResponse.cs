namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

using System.Net;
using System.Text.Json;

/// <summary>What a <c>/tokenize</c> or <c>/v1/messages/count_tokens</c> call returned, unjudged, so the caller applies its own endpoint and redirect policy.</summary>
/// <remarks>
///     The body stays unread until <see cref="ReadTokenCountAsync" />, so the caller's status and endpoint checks run
///     before any byte of it is parsed. Owns the underlying response: dispose it.
/// </remarks>
public sealed class LlamaServerTokenizeResponse : IDisposable
{
    private readonly HttpResponseMessage _response;

    internal LlamaServerTokenizeResponse(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        _response = response;
    }

    public HttpStatusCode StatusCode => _response.StatusCode;

    public bool IsSuccessStatusCode => _response.IsSuccessStatusCode;

    /// <summary>The URI the response was finally produced for, when the transport reported one.</summary>
    public Uri? FinalRequestUri => _response.RequestMessage?.RequestUri;

    /// <summary>
    ///     Reads the body and returns the length of its <c>tokens</c> array (<c>/tokenize</c>) or its positive integer
    ///     <c>input_tokens</c> (<c>count_tokens</c>), or <see langword="null" /> when the body was not JSON or held neither.
    /// </summary>
    public async Task<int?> ReadTokenCountAsync(CancellationToken ct)
    {
        await using var stream = await _response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("tokens", out var tokens))
            {
                return tokens.ValueKind == JsonValueKind.Array && tokens.GetArrayLength() > 0 ? tokens.GetArrayLength() : null;
            }

            return root.TryGetProperty("input_tokens", out var inputTokens)
                   && inputTokens.ValueKind == JsonValueKind.Number
                   && inputTokens.TryGetInt32(out var count)
                   && count > 0
                ? count
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _response.Dispose();
}
