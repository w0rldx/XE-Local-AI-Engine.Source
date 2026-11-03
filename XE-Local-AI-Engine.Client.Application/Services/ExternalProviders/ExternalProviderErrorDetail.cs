namespace XE_Local_AI_Engine.Client.Services.ExternalProviders;

using System.Text.Json;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

/// <summary>
///     The code and message an OpenAI-compatible provider or gateway put in an error body, reduced to text the operator
///     may see.
/// </summary>
/// <remarks>
///     Only a JSON object is read: message from <c>error.message</c> or <c>message</c>; reason from <c>reason</c>, standing in
///     for a missing message; code from <c>error.code</c>, a string <c>error</c> or <c>code</c>. A message or reason survives
///     only as plain words (letters with inner hyphens or apostrophes, or digits, edged by <c>.,;:()'</c>) and a code only as a
///     lowercase identifier, hyphenated words of letters after a first word of at least three (<c>sk-live-abc</c> stays out), or one of
///     <see cref="KnownMixedCaseCodes" />, so a key, token, URL or path is dropped whole. A secret of letters alone still passes; only a caller knowing the credential can drop it, through <c>forbidden</c> or <see cref="SecretEchoGuardHandler" />.
/// </remarks>
internal readonly record struct ExternalProviderErrorDetail(string? Code, string? Message, string? Reason = null)
{
    internal const int MaxLength = 300;

    /// <summary>The gateway's backend (not the caller's credential) lacks the role for this model or operation.</summary>
    internal const string PermissionDeniedCode = "PermissionDenied";

    /// <summary>The model exists, but not on the API surface (Chat Completions or Responses) the request used.</summary>
    internal const string ApiNotSupportedCode = "api_not_supported";

    /// <summary>The bearer token was refused, typically because it expired.</summary>
    internal const string InvalidTokenCode = "invalid_token";

    /// <summary>A period's token quota is used up, as opposed to a per-minute limit, which a short wait clears.</summary>
    internal const string TokenQuotaExceededCode = "token_quota_exceeded";

    /// <summary>What to say about a 403 that does not blame the credential: a project, membership, tenant, quota or organization rule a new key would not change.</summary>
    internal const string DeniedMessage = "The provider or gateway denied this request. Check with its admin what this connection may use.";

    private static readonly Regex Word = new("^(?:[A-Za-z]+(?:[-'][A-Za-z]+)*|[0-9]+)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex CodeIdentifier = new("^(?=.{1,40}$)(?:[a-z][a-z0-9_]*|[a-z]{3,}(?:-[a-z]+)+)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // Mixed-case codes a real gateway sends, kept by exact match because the lowercase rule would drop them.
    private static readonly string[] KnownMixedCaseCodes = [PermissionDeniedCode];

    // Codes that blame the credential itself: a gateway's own, and the common provider spellings of a bad key or token.
    private static readonly string[] BadCredentialCodes =
    [
        InvalidTokenCode, "invalid_api_key", "invalid_key", "expired_token", "token_expired", "invalid_authentication", "authentication_error",
        "api-key-invalid", "api-key-revoked", "api-key-expired"
    ];

    /// <summary>
    ///     "code: message", the message alone, the code alone, or <see langword="null" /> when the body carried neither, then the reason when a
    ///     message left it over; capped at <see cref="MaxLength" />.
    /// </summary>
    public string? Text
    {
        get
        {
            var text = Code is null || Message is null ? Message ?? Code : $"{Code}: {Message}";
            return Cap(Reason is null ? text : $"{text} (reason: {Reason})");
        }
    }

    /// <summary>Whether the code says the credential itself is bad, so replacing it can help. Any other 403 is a denial a new key would not change.</summary>
    public bool ReportsBadCredential => Code is not null && BadCredentialCodes.Contains(Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Whether an API gateway said no operation matches the request path: a wrong base URL, which no retry can fix.
    ///     Matched on the message alone, as the code a gateway pairs it with (<c>apim_policy_error</c>) also covers other policy faults.
    /// </summary>
    public bool ReportsNoRoute => Message?.Contains("Unable to match incoming request to an operation", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    ///     Whether a gateway said it does not support the requested operation. Matched on the message alone and under any
    ///     status, as the status a gateway sends it with is not pinned down; the same request fails the same way on a retry.
    /// </summary>
    public bool ReportsUnsupportedOperation => Message?.Contains("The requested operation is unsupported", StringComparison.OrdinalIgnoreCase) == true;

    /// <param name="body">The raw error body.</param>
    /// <param name="forbidden">Values (sent credentials) that void the whole detail when the body carries one anywhere, decoded, before sanitizing.</param>
    public static ExternalProviderErrorDetail Parse(ReadOnlyMemory<byte> body, IReadOnlyList<string>? forbidden = null)
    {
        if (body.IsEmpty || (forbidden is not null && SecretEchoGuardHandler.Echoes(body, forbidden)))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            var error = root.TryGetProperty("error", out var errorElement) ? errorElement : default;
            var errorMessage = error.ValueKind == JsonValueKind.Object ? GetString(error, "message") : null;
            var rootMessage = GetString(root, "message");
            var rawReason = GetString(root, "reason");
            var rawCode = error.ValueKind switch
            {
                JsonValueKind.Object => GetString(error, "code"),
                JsonValueKind.String => error.GetString(),
                _ => GetString(root, "code")
            };

            var code = SanitizeCode(rawCode);
            var message = SanitizeMessage(errorMessage) ?? SanitizeMessage(rootMessage);
            var reason = SanitizeMessage(rawReason);
            if (message is null)
            {
                message = reason;
                reason = null;
            }

            return new ExternalProviderErrorDetail(code, message == code ? null : message, reason == message || reason == code ? null : reason);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? SanitizeMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var words = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.All(static word => Word.IsMatch(word.Trim('.', ',', ';', ':', '(', ')', '\'')))
            ? Cap(string.Join(' ', words))
            : null;
    }

    private static string? SanitizeCode(string? value)
    {
        return value is not null && (CodeIdentifier.IsMatch(value) || KnownMixedCaseCodes.Contains(value, StringComparer.Ordinal)) ? value : null;
    }

    // Cut on the last word boundary inside the cap, so a capped detail never ends in half a word.
    private static string? Cap(string? value)
    {
        if (value is not { Length: > MaxLength })
        {
            return value;
        }

        var lastSpace = value.LastIndexOf(' ', MaxLength);
        return value[..(lastSpace > 0 ? lastSpace : MaxLength)];
    }
}
