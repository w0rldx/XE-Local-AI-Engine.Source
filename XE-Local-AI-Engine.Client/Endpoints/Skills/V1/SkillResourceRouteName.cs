namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using System.Text.RegularExpressions;

/// <summary>
///     Decodes and validates a bundled-resource name bound from the <c>{resourceName}</c> route segment.
/// </summary>
/// <remarks>
///     A resource name is a skill-root-relative path (<c>references/FAQ.md</c>), so it carries slashes. The React
///     client escapes the segment with <c>encodeURIComponent</c>, and ASP.NET Core / Kestrel deliberately leaves
///     encoded slashes (<c>%2F</c>) and backslashes (<c>%5C</c>) ENCODED to defeat path-segment smuggling — so the
///     endpoint receives literal <c>%2F</c> and must decode before the lookup. Same problem and same fix as
///     <c>ModelRouteName</c> on the local-model routes.
///     <para>
///         Validation mirrors the guard the import pipeline applies when it writes a resource: an ASCII path charset,
///         a length cap and an explicit <c>..</c> rejection. Nothing here touches the filesystem — the name is matched
///         against stored rows — so the guard is not a containment control; it exists so a name carrying a newline, a
///         control character or a homoglyph is refused at the boundary rather than reaching a log line or an operator's
///         screen. Decoding runs FIRST, so a smuggled <c>..%2F..</c> is rejected after it decodes to <c>../..</c>.
///     </para>
/// </remarks>
internal static partial class SkillResourceRouteName
{
    private const int MaxLength = 200;

    /// <summary>Returns the decoded name, or <c>null</c> when the segment is missing or fails the charset guard.</summary>
    public static string? DecodeAndValidate(string? routeValue)
    {
        if (string.IsNullOrEmpty(routeValue))
        {
            return null;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(routeValue);
        }
        catch (UriFormatException)
        {
            return null;
        }

        return decoded.Length is > 0 and <= MaxLength
               && NamePattern().IsMatch(decoded)
               && !decoded.Split('/').Contains("..", StringComparer.Ordinal)
            ? decoded
            : null;
    }

    [GeneratedRegex("^(?:[A-Za-z0-9._-]+/)*[A-Za-z0-9._-]+$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex NamePattern();
}
