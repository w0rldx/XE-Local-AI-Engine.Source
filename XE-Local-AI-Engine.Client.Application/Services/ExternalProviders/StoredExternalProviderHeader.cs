namespace XE_Local_AI_Engine.Client.Services.ExternalProviders;

using System.Text;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

/// <summary>
///     One custom request header on an external connection. A secret value is write-only: never returned by the API and
///     redacted from <c>ToString</c>.
/// </summary>
public sealed record StoredExternalProviderHeader
{
    /// <summary>The header name: an RFC 7230 token outside the reserved set, enforced by validation.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The header value. Blank on a save request means "keep the stored value" for a secret row.</summary>
    public string? Value { get; init; }

    /// <summary>True when the value is a secret.</summary>
    public bool IsSecret { get; init; }

    /// <summary>
    ///     Every <see cref="CustomHeaderRules" /> violation of the rows, naming headers and never values. The one entry
    ///     point the store and the endpoint validators share.
    /// </summary>
    public static IReadOnlyList<string> FindViolations(IReadOnlyList<StoredExternalProviderHeader> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        return CustomHeaderRules.FindViolations([.. headers.Select(static header => new KeyValuePair<string?, string?>(header.Name, header.Value))]);
    }

    // The sealed-record PrintMembers signature is private and redacts secret values from ToString.
    private bool PrintMembers(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Append("Name = ").Append(Name);
        builder.Append(", Value = ").Append(IsSecret ? "[REDACTED]" : Value);
        builder.Append(", IsSecret = ").Append(IsSecret);
        return true;
    }
}
