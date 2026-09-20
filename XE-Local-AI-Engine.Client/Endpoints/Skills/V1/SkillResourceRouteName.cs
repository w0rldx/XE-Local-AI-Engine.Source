namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using System.Text.RegularExpressions;

/// <summary>
///     Decodes and validates a bundled-resource name bound from the <c>{resourceName}</c> route segment.
/// </summary>
/// <remarks>
///     A resource name is a skill-root-relative path (<c>references/FAQ.md</c>), so it carries slashes and reaches the endpoint still carrying a literal <c>%2F</c> — the
///     same problem and fix as <c>ModelRouteName</c>; docs/wiki/09-api-and-hubs.md ("Conventions") states the rule. Validation mirrors the import pipeline's own guard: an
///     ASCII path charset, a length cap and an explicit <c>..</c> rejection. Nothing here touches the filesystem, so it is not a containment control: it refuses a name
///     carrying a newline, a control character or a homoglyph. Decoding runs FIRST, so a smuggled <c>..%2F..</c> is rejected once it decodes to <c>../..</c>.
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
