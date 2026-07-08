namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     How an <see cref="AzureFoundryAuthMode.EntraId" /> connection with no configured client secret authenticates
///     an interactive user. Ignored when a client secret is configured — app-only client-credentials always wins.
/// </summary>
public enum EntraSignInMethod
{
    /// <summary>App-only client-credentials via the configured client secret. Derived default when a secret is present.</summary>
    ClientSecret = 0,

    /// <summary>Interactive user sign-in via the OAuth 2.0 device-code flow.</summary>
    DeviceCode = 1,

    /// <summary>Interactive user sign-in via a browser window opened on the node machine.</summary>
    InteractiveBrowser = 2,

    /// <summary>
    ///     Confidential-client authorization-code sign-in ("Postman parity"): browser sign-in yields a DELEGATED
    ///     token (scp claim) while the stored client secret authenticates the code redemption via MSAL
    ///     <c>ConfidentialClientApplication</c>. Unlike <see cref="ClientSecret" />, this mode requires a client
    ///     secret AND uses a delegated token scope — the app-only <c>/.default</c> fail-fast does not apply.
    /// </summary>
    AuthorizationCode = 3,
}
