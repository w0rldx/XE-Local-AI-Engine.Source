namespace XE_Local_AI_Engine.Client.Services.ExternalProviders;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
///     The code and message an OpenAI-compatible provider or gateway put in an error body, reduced to text the operator
///     may see.
/// </summary>
/// <remarks>
///     Only a JSON object is read: message from <c>error.message</c>, <c>message</c> or <c>reason</c>; code from
///     <c>error.code</c>, a string <c>error</c> or <c>code</c>. A message survives only as plain words (letters with inner
///     hyphens, or digits, edged by <c>.,;:()'</c>) and a code only as a lowercase identifier, so a key, token, URL or path
///     is dropped whole. A secret of letters alone still passes; only a caller knowing the credential can drop it.
/// </remarks>
internal readonly record struct ExternalProviderErrorDetail(string? Code, string? Message)
{
    internal const int MaxLength = 300;

    private static readonly Regex Word = new("^(?:[A-Za-z]+(?:-[A-Za-z]+)*|[0-9]+)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex CodeIdentifier = new("^[a-z][a-z0-9_]{0,39}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>"code: message", the message alone, the code alone, or <see langword="null" /> when the body carried neither; capped at <see cref="MaxLength" />.</summary>
    public string? Text => Cap(Code is null || Message is null ? Message ?? Code : $"{Code}: {Message}");

    /// <param name="body">The raw error body.</param>
    /// <param name="forbidden">Values (sent credentials) that void the whole detail when any decoded candidate contains one, before sanitizing.</param>
    public static ExternalProviderErrorDetail Parse(ReadOnlyMemory<byte> body, IReadOnlyList<string>? forbidden = null)
    {
        if (body.IsEmpty)
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
            var reason = GetString(root, "reason");
            var rawCode = error.ValueKind switch
            {
                JsonValueKind.Object => GetString(error, "code"),
                JsonValueKind.String => error.GetString(),
                _ => GetString(root, "code")
            };

            string?[] candidates = [errorMessage, rootMessage, reason, rawCode];
            if (forbidden is not null
                && candidates.Any(candidate => candidate is not null
                                               && forbidden.Any(value => !string.IsNullOrWhiteSpace(value) && candidate.Contains(value, StringComparison.OrdinalIgnoreCase))))
            {
                return default;
            }

            var message = SanitizeMessage(errorMessage) ?? SanitizeMessage(rootMessage) ?? SanitizeMessage(reason);
            return new ExternalProviderErrorDetail(SanitizeCode(rawCode), message);
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
        return value is not null && CodeIdentifier.IsMatch(value) ? value : null;
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
