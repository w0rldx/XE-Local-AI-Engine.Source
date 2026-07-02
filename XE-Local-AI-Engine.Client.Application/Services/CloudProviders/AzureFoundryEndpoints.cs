namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Host-allowlist guard for Azure Foundry / Azure OpenAI endpoints.
/// </summary>
/// <remarks>
///     Shared by store validation and the chat-client factory so a managed-identity Entra token can never be
///     sent to an arbitrary operator-entered host (MEDIUM-4). Operators may widen the allowlist with shape-guarded
///     extra suffixes (Locked #14) — e.g. an APIM gateway host — but the built-in Azure suffixes are never removable.
/// </remarks>
public static class AzureFoundryEndpoints
{
    private const int MaxHostSuffixLength = 253;

    private static readonly string[] AllowedHostSuffixes =
    [
        ".openai.azure.com",
        ".services.ai.azure.com",
        ".cognitiveservices.azure.com",
    ];

    /// <summary>
    ///     Returns true when the endpoint host ends with a known Azure suffix (case-insensitive). Equivalent to
    ///     <see cref="IsAllowedHost(Uri, IEnumerable{string})" /> with no operator suffixes.
    /// </summary>
    public static bool IsAllowedHost(Uri endpoint)
    {
        return IsAllowedHost(endpoint, []);
    }

    /// <summary>
    ///     Returns true when the endpoint host ends with a built-in Azure suffix OR a shape-valid operator-added suffix
    ///     (case-insensitive, Locked #14). Malformed operator suffixes never widen the allowlist.
    /// </summary>
    public static bool IsAllowedHost(Uri endpoint, IEnumerable<string> extraSuffixes)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(extraSuffixes);

        if (AllowedHostSuffixes.Any(suffix => endpoint.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return extraSuffixes.Any(suffix =>
            ValidateHostSuffix(suffix)
            && endpoint.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Shape guard for an operator-added allowed host suffix (Locked #14). A valid suffix starts with <c>.</c>, has
    ///     at least two non-empty dot-separated DNS labels (so <c>.azure-api.net</c> ✓ but a bare TLD <c>.com</c> ✗),
    ///     is at most 253 characters, contains only DNS label characters, and carries no wildcard.
    /// </summary>
    public static bool ValidateHostSuffix(string? suffix)
    {
        if (string.IsNullOrEmpty(suffix)
            || suffix.Length > MaxHostSuffixLength
            || suffix[0] != '.'
            || suffix.Contains('*', StringComparison.Ordinal))
        {
            return false;
        }

        var labels = suffix[1..].Split('.');
        if (labels.Length < 2)
        {
            return false;
        }

        return labels.All(IsValidDnsLabel);
    }

    private static bool IsValidDnsLabel(string label)
    {
        if (label.Length == 0 || label[0] == '-' || label[^1] == '-')
        {
            return false;
        }

        return label.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-');
    }
}
