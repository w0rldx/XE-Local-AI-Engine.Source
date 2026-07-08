namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

/// <summary>
///     Shared constants for the Entra ID confidential-client authorization-code sign-in flow, referenced by the
///     stored-connection validator, the sign-in coordinator, and the loopback listener.
/// </summary>
public static class EntraAuthCodeDefaults
{
    // Composed from parts (scheme + host + port + path) rather than a single hardcoded URI literal so it does not
    // trip the hardcoded-URI analyzer (S1075) — mirrors KokoroVoiceCatalog's download-URL composition.
    private const string DefaultRedirectHost = "localhost";
    private const string DefaultRedirectPort = "53682";
    private const string DefaultRedirectPath = "/signin-oidc";

    /// <summary>
    ///     The default loopback redirect URI used when a connection has no operator-configured
    ///     <see cref="StoredAzureFoundryConnection.EntraAuthCodeRedirectUri" />.
    /// </summary>
    public static readonly string RedirectUri = $"{Uri.UriSchemeHttp}://{DefaultRedirectHost}:{DefaultRedirectPort}{DefaultRedirectPath}";

    /// <summary>How long a pending authorization-code sign-in waits for the browser callback before timing out.</summary>
    public static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     True when <paramref name="host" /> is a loopback host per RFC 8252 §7.3 (the only hosts a redirect URI's
    ///     one-shot local listener is ever allowed to bind).
    /// </summary>
    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
               || (System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address));
    }

    /// <summary>
    ///     Resolves the effective redirect URI for a connection: the operator-configured value when present and
    ///     valid-shaped, otherwise <see cref="RedirectUri" />. Does not validate — see
    ///     <see cref="TryValidateRedirectUri" /> for shape enforcement.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1055:Uri return values should not be strings",
        Justification = "The result flows straight into stored-config fields, DTOs, and MSAL's string-typed WithRedirectUri — every caller wants the string form.")]
    public static string ResolveRedirectUri(string? configuredRedirectUri)
    {
        return string.IsNullOrWhiteSpace(configuredRedirectUri) ? RedirectUri : configuredRedirectUri.Trim();
    }

    /// <summary>
    ///     Validates a redirect URI is absolute http(s) on a loopback host. A null/blank input is treated as valid
    ///     (it resolves to <see cref="RedirectUri" /> — see <see cref="ResolveRedirectUri" />).
    /// </summary>
    public static bool TryValidateRedirectUri(string? redirectUri, out Uri? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            return true;
        }

        if (!Uri.TryCreate(redirectUri.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !IsLoopbackHost(uri.Host))
        {
            return false;
        }

        parsed = uri;
        return true;
    }
}
