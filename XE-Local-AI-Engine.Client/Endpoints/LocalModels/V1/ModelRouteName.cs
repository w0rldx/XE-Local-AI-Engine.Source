namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

/// <summary>
///     Decodes a model name that was bound from a <c>{modelName}</c> route segment.
/// </summary>
/// <remarks>
///     Kestrel route binding decodes most percent-octets but, by security design, leaves an encoded slash
///     (<c>%2F</c>) or backslash (<c>%5C</c>) ENCODED to defeat path-segment smuggling, so a Hugging Face reference
///     the client escaped with <c>encodeURIComponent</c> arrives carrying a literal <c>%2F</c> whose <c>%</c> fails
///     <c>ModelNameValidator</c>. Decoding here restores the canonical name BEFORE validation and the service call.
///     Whole rule: docs/wiki/09-api-and-hubs.md ("Conventions").
/// </remarks>
internal static class ModelRouteName
{
    public static string? Decode(string? routeValue)
    {
        // Uri.UnescapeDataString, never WebUtility.UrlDecode: the latter turns `+` into a space and would corrupt a name carrying a literal `+`. Decoding is idempotent for an
        // already-decoded plain name, and ModelNameValidator's path-traversal and scheme guards still run AFTER it, so a smuggled `..%2F..` or `%5C` stays rejected.
        return routeValue is null ? null : Uri.UnescapeDataString(routeValue);
    }
}
