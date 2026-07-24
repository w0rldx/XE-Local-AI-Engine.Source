namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

using System.Text;

/// <summary>
///     A single custom HTTP request header appended to every outbound Azure Foundry / Azure OpenAI request on a
///     connection (chat, streaming), for both auth modes.
/// </summary>
/// <remarks>
///     No <c>required</c> members so a partial / legacy JSON parse never throws. A header value may be marked
///     <see cref="IsSecret" />: secret values are write-only (never returned to the UI) and redacted in
///     <see cref="object.ToString" /> via <see cref="PrintMembers" /> (Locked #11).
/// </remarks>
public sealed record StoredAzureFoundryHeader
{
    /// <summary>
    ///     The header name. Non-blank, an RFC 7230 token, and not a reserved name — enforced by validation, not by
    ///     deserialization.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    ///     The header value. Null when a secret header row is edited without re-supplying the stored value (the prior
    ///     value is merged back in the endpoint before persistence).
    /// </summary>
    public string? Value { get; init; }

    /// <summary>
    ///     True when the value is a secret (write-only): never returned in a response, redacted in <c>ToString</c>.
    /// </summary>
    public bool IsSecret { get; init; }

    // Sealed-record PrintMembers signature is private (Locked #11). Redacts a secret value so it never leaks via ToString.
    private bool PrintMembers(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Append("Name = ").Append(Name);
        builder.Append(", Value = ").Append(IsSecret ? "[REDACTED]" : Value);
        builder.Append(", IsSecret = ").Append(IsSecret);
        return true;
    }
}
