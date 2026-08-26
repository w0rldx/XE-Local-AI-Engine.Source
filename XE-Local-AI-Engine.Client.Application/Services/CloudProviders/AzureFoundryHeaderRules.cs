namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Shared validation rules for custom Azure Foundry request headers. Enforced both endpoint-side (name-only
///     <c>ValidationProblem</c> messages) and in <c>CloudCredentialStore.ValidateConfig</c> as defense-in-depth, and
///     the reserved set is reused by the outbound pipeline policy as a defense-in-depth skip.
/// </summary>
public static class AzureFoundryHeaderRules
{
    /// <summary>Maximum number of custom headers per connection.</summary>
    public const int MaxHeaderCount = 32;

    /// <summary>Maximum header-name length in characters.</summary>
    public const int MaxHeaderNameLength = 128;

    /// <summary>Maximum header-value length in characters.</summary>
    public const int MaxHeaderValueLength = 4096;

    /// <summary>Maximum number of operator-added allowed host suffixes per connection.</summary>
    public const int MaxHostSuffixCount = 16;

    // Case-insensitive reserved set: names that carry authentication or transport semantics and must never be
    // operator-overridable. Rejected on save AND defensively skipped by the outbound policy.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "api-key",
        "Authorization",
        "Host",
        "Content-Type",
        "Content-Length",
        "Content-Encoding",
        "Cookie",
        "Proxy-Authorization",
        "Transfer-Encoding",
        "Connection",
        "Expect",
    };

    /// <summary>Returns true when the name is in the case-insensitive reserved set.</summary>
    public static bool IsReservedName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return ReservedNames.Contains(name);
    }

    /// <summary>
    ///     Returns true when the name is a non-empty RFC 7230 token:
    ///     <c>A-Z a-z 0-9 ! # $ % &amp; ' * + - . ^ _ ` | ~</c>.
    /// </summary>
    public static bool IsValidHeaderName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        return name.All(IsTokenChar);
    }

    /// <summary>
    ///     Returns true when the value contains only RFC 7230 field-value characters: rejects CR, LF, NUL,
    ///     all control chars <c>0x00–0x1F</c> except HTAB (<c>0x09</c>), and DEL (<c>0x7F</c>). Null / empty is allowed
    ///     (a blank value is a distinct case handled by merge + secret-resolvable validation).
    /// </summary>
    public static bool IsValidHeaderValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        foreach (var character in value)
        {
            if (character == '\t')
            {
                continue;
            }

            if (character < ' ' || character == '\u007F')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTokenChar(char character)
    {
        if (char.IsAsciiLetterOrDigit(character))
        {
            return true;
        }

        return character is '!' or '#' or '$' or '%' or '&' or '\'' or '*'
            or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
    }
}
