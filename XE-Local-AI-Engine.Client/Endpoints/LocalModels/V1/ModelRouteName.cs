namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

/// <summary>
///     Decodes a model name that was bound from a <c>{modelName}</c> route segment.
/// </summary>
/// <remarks>
///     WHY this exists: the React client's hey-api generated path serializer escapes the segment with
///     <c>encodeURIComponent(modelName)</c>, so a Hugging Face reference such as
///     <c>hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL</c> reaches the server as
///     <c>hf.co%2Funsloth%2Fgemma-4-12b-it-GGUF%3AUD-Q4_K_XL</c>. ASP.NET Core / Kestrel route binding decodes most
///     percent-octets (so <c>%3A</c> becomes <c>:</c>) but, by security design, leaves encoded slashes (<c>%2F</c>) and
///     backslashes (<c>%5C</c>) ENCODED to defeat path-segment smuggling. The endpoint therefore receives a value still
///     containing literal <c>%2F</c>, whose <c>%</c> fails <c>ModelNameValidator</c> and surfaces as "Invalid model
///     identifier" — while plain tags like <c>llama3:8b</c> (no encoded slash) arrive fully decoded and pass.
///     Decoding here restores the canonical model name BEFORE validation and BEFORE the service call.
///     <para>
///         Uses <see cref="System.Uri.UnescapeDataString(string)" /> rather than <c>WebUtility.UrlDecode</c> because the
///         latter turns <c>+</c> into a space, which would corrupt names containing a literal <c>+</c>. Decoding is
///         idempotent for already-decoded plain names (<c>llama3:8b</c> stays <c>llama3:8b</c>), so non-HF models are
///         unaffected. The path-traversal / scheme guards in <c>ModelNameValidator</c> still run AFTER the decode, so a
///         smuggled <c>..%2F..</c> (which decodes to <c>../..</c>) and <c>%5C</c> (which decodes to <c>\</c>) remain
///         rejected.
///     </para>
/// </remarks>
internal static class ModelRouteName
{
    public static string? Decode(string? routeValue)
    {
        return routeValue is null ? null : Uri.UnescapeDataString(routeValue);
    }
}
