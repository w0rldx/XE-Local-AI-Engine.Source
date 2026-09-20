namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

/// <summary>
///     Coordinates the Operator-facing Entra ID authorization-code sign-in lifecycle for the stored Azure Foundry
///     connection: start (or supersede) a browser sign-in, and expose a pollable status.
/// </summary>
/// <remarks>
///     The authorize URL comes back immediately while the loopback callback and code redemption complete in the
///     background. Distinct from the silent-only credential
///     <see cref="Implementation.AzureFoundryChatClientFactory" /> builds for chat sends: this owns the one-time
///     interactive sign-in that produces the persisted delegated credential. Mirrors
///     <see cref="IEntraDeviceCodeSignInCoordinator" />.
/// </remarks>
public interface IEntraAuthCodeSignInCoordinator
{
    /// <summary>
    ///     Starts (superseding any in-flight attempt) an authorization-code sign-in against the stored connection's
    ///     tenant / client / secret / redirect URI and returns the authorize URL to open in a browser.
    /// </summary>
    /// <remarks>
    ///     The exchange completes in the background; poll <see cref="GetStatus" />. Throws
    ///     <see cref="InvalidOperationException" /> when no Entra ID connection configured for authorization-code
    ///     sign-in (tenant id, client id, client secret, and a loopback redirect URI) is stored.
    /// </remarks>
    Task<EntraAuthCodeSignInHandle> StartAsync(CancellationToken cancellationToken);

    /// <summary>Returns the current sign-in status snapshot. Carries no token material.</summary>
    EntraAuthCodeSignInStatus GetStatus();
}
