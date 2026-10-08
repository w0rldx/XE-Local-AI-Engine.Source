namespace XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

using System.Text;

/// <summary>
///     Provider-neutral validation rules for operator-defined custom request headers: limits, the reserved-name set and
///     the RFC 7230 name and value grammar.
/// </summary>
/// <remarks>
///     The one home for these rules. The Azure Foundry connection and the external OpenAI-compatible connection both
///     validate against them on save and in their stores, and the outbound header policy and handler reuse the
///     reserved set as a defense-in-depth skip.
/// </remarks>
public static class CustomHeaderRules
{
    /// <summary>Maximum number of custom headers per connection.</summary>
    public const int MaxHeaderCount = 32;

    /// <summary>Maximum header-name length in characters.</summary>
    public const int MaxHeaderNameLength = 128;

    /// <summary>Maximum header-value length in characters.</summary>
    public const int MaxHeaderValueLength = 4096;

    /// <summary>Maximum number of operator-added allowed host suffixes per connection.</summary>
    public const int MaxHostSuffixCount = 16;

    /// <summary>The refusal for a row that has a value but no name.</summary>
    public const string ValueWithoutNameMessage = "A custom header value was provided without a header name.";

    // Case-insensitive reserved set: names that carry authentication or transport semantics and must never be
    // operator-overridable, plus the content headers .NET refuses on a request (it throws "Misused header name").
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
        "Allow",
        "Content-Disposition",
        "Content-Language",
        "Content-Location",
        "Content-MD5",
        "Content-Range",
        "Expires",
        "Last-Modified",
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
    ///     Returns true when the value contains only RFC 7230 field-value characters.
    /// </summary>
    /// <remarks>
    ///     Rejects CR, LF, NUL, all control chars <c>0x00–0x1F</c> except HTAB (<c>0x09</c>), and DEL (<c>0x7F</c>).
    ///     Null / empty is allowed: a blank value is a distinct case handled by merge + secret-resolvable validation.
    /// </remarks>
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

    /// <summary>
    ///     Every rule violation of a header set, in declaration order; empty when the set is storable. Messages name the
    ///     header, never its value. A row with neither name nor value is ignored (an empty editor row).
    /// </summary>
    /// <param name="headers">The rows as name and value pairs, untrimmed.</param>
    public static IReadOnlyList<string> FindViolations(IReadOnlyCollection<KeyValuePair<string?, string?>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var errors = new List<string>();
        if (headers.Count > MaxHeaderCount)
        {
            errors.Add($"A maximum of {MaxHeaderCount} custom headers is allowed.");
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawName, value) in headers)
        {
            var name = rawName?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    errors.Add(ValueWithoutNameMessage);
                }

                continue;
            }

            if (name.Length > MaxHeaderNameLength)
            {
                errors.Add($"Custom header name '{name}' exceeds {MaxHeaderNameLength} characters.");
            }
            else if (!IsValidHeaderName(name))
            {
                errors.Add($"Custom header name '{name}' contains invalid characters.");
            }
            else if (IsReservedName(name))
            {
                errors.Add($"Custom header name '{name}' is reserved and cannot be set.");
            }
            else if (!seenNames.Add(name))
            {
                errors.Add($"Custom header name '{name}' is duplicated.");
            }

            if ((value?.Length ?? 0) > MaxHeaderValueLength)
            {
                errors.Add($"Custom header '{name}' value exceeds {MaxHeaderValueLength} characters.");
            }
            else if (!IsValidHeaderValue(value))
            {
                errors.Add($"Custom header '{name}' value contains invalid control characters.");
            }
            else if (value is not null && !Ascii.IsValid(value))
            {
                // The transport refuses a non-ASCII header value at send time, so refuse it at save instead.
                errors.Add($"Custom header '{name}' value contains non-ASCII characters.");
            }
        }

        return errors;
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
