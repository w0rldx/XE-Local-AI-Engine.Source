namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

using System.Text;

/// <summary>
///     A single Azure Foundry / Azure OpenAI connection holding N manually-added deployments.
/// </summary>
/// <remarks>
///     No <c>required</c> members so a partial / legacy JSON parse never throws. The API key is
///     null when <see cref="AuthMode" /> is <see cref="AzureFoundryAuthMode.ManagedIdentity" />.
/// </remarks>
public sealed record StoredAzureFoundryConnection
{
    /// <summary>
    ///     The connection endpoint (absolute HTTPS, Azure host).
    /// </summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>
    ///     The authentication mode for this connection.
    /// </summary>
    public AzureFoundryAuthMode AuthMode { get; init; }

    /// <summary>
    ///     The wire surface this connection targets. Default <see cref="AzureFoundryApiSurface.AzureDeployments" /> so
    ///     a legacy stored connection with no <c>ApiSurface</c> field deserializes to the pre-existing behavior.
    /// </summary>
    public AzureFoundryApiSurface ApiSurface { get; init; }

    /// <summary>
    ///     The encrypted API key. Null when <see cref="AuthMode" /> is
    ///     <see cref="AzureFoundryAuthMode.ManagedIdentity" />.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    ///     The deployments manually added to this connection.
    /// </summary>
    public IReadOnlyList<StoredAzureFoundryModel> Models { get; init; } = [];

    /// <summary>
    ///     Custom HTTP headers appended to every outbound request on this connection (both auth modes). Default empty;
    ///     legacy blobs with no <c>Headers</c> field deserialize to an empty list.
    /// </summary>
    public IReadOnlyList<StoredAzureFoundryHeader> Headers { get; init; } = [];

    /// <summary>
    ///     Operator-added extra allowed host suffixes, such as an APIM gateway host. Combined with the built-in
    ///     Azure suffixes to form the effective endpoint allowlist. Not secret — round-trips to the UI. Default empty.
    /// </summary>
    public IReadOnlyList<string> AdditionalAllowedHostSuffixes { get; init; } = [];

    /// <summary>The Entra ID tenant id. Required when <see cref="AuthMode" /> is <see cref="AzureFoundryAuthMode.EntraId" />.</summary>
    public string? EntraTenantId { get; init; }

    /// <summary>The Entra ID application (client) id. Required when <see cref="AuthMode" /> is <see cref="AzureFoundryAuthMode.EntraId" />.</summary>
    public string? EntraClientId { get; init; }

    /// <summary>
    ///     The encrypted Entra ID client secret for app-only client-credentials. Null selects interactive user
    ///     sign-in via <see cref="EntraSignInMethod" /> instead.
    /// </summary>
    public string? EntraClientSecret { get; init; }

    /// <summary>
    ///     The OAuth2 token scope requested for the Entra ID token (e.g. <c>api://&lt;backend-app-id&gt;/.default</c>).
    ///     Required when <see cref="AuthMode" /> is <see cref="AzureFoundryAuthMode.EntraId" />.
    /// </summary>
    public string? EntraTokenScope { get; init; }

    /// <summary>The interactive sign-in method used when no <see cref="EntraClientSecret" /> is configured.</summary>
    public EntraSignInMethod EntraSignInMethod { get; init; }

    /// <summary>
    ///     The loopback redirect URI for <see cref="CloudProviders.EntraSignInMethod.AuthorizationCode" /> sign-in
    ///     (e.g. <c>http://localhost:53682/signin-oidc</c>). Not secret — round-trips to the UI. Null or blank
    ///     selects the default <see cref="EntraAuthCodeDefaults.RedirectUri" />. Ignored for every other sign-in
    ///     method.
    /// </summary>
    public string? EntraAuthCodeRedirectUri { get; init; }

    // The sealed-record PrintMembers signature is private. It redacts the API key and Entra client secret and
    // delegates per-header secret redaction (each header's own ToString redacts) so no secret value or key ever
    // leaks via ToString.
    private bool PrintMembers(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Append("Endpoint = ").Append(Endpoint);
        builder.Append(", AuthMode = ").Append(AuthMode);
        builder.Append(", ApiSurface = ").Append(ApiSurface);
        builder.Append(", ApiKey = ").Append(ApiKey is null ? "null" : "[REDACTED]");
        builder.Append(", Models = [").AppendJoin(", ", Models).Append(']');
        builder.Append(", Headers = [").AppendJoin(", ", Headers).Append(']');
        builder.Append(", AdditionalAllowedHostSuffixes = [").AppendJoin(", ", AdditionalAllowedHostSuffixes).Append(']');
        builder.Append(", EntraTenantId = ").Append(EntraTenantId);
        builder.Append(", EntraClientId = ").Append(EntraClientId);
        builder.Append(", EntraClientSecret = ").Append(EntraClientSecret is null ? "null" : "[REDACTED]");
        builder.Append(", EntraTokenScope = ").Append(EntraTokenScope);
        builder.Append(", EntraSignInMethod = ").Append(EntraSignInMethod);
        builder.Append(", EntraAuthCodeRedirectUri = ").Append(EntraAuthCodeRedirectUri);
        return true;
    }
}
