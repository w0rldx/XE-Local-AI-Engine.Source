namespace XE_Local_AI_Engine.Client.Services.Common;

/// <summary>
///     Whether an artifact's bytes can be handed over as text. Decided from the declared MEDIA TYPE and never by
///     sniffing the bytes, so binary content is never delivered as mangled UTF-8 — and so two producers of the same
///     artifact family cannot disagree about one file.
///     <para>
///         Shared by the work-session and development-workflow artifact reads because both answer the same wire
///         question (<c>isBase64</c>), and a second copy of the list is a second answer waiting to drift.
///     </para>
/// </summary>
public static class ArtifactMediaTypes
{
    private static readonly string[] TextPrefixes = ["text/"];

    private static readonly string[] TextTypes =
    [
        "application/json",
        "application/xml",
        "application/x-ndjson",
        "application/javascript",
        "application/sql",
        "application/x-yaml"
    ];

    public static bool IsText(string mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);

        var bare = mediaType.Split(';')[0].Trim();
        return TextPrefixes.Any(prefix => bare.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
               || TextTypes.Contains(bare, StringComparer.OrdinalIgnoreCase)
               || bare.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
               || bare.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);
    }
}
