namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

/// <summary>
///     Coordinates the Operator-facing Entra ID device-code sign-in lifecycle for the stored Azure Foundry
///     connection: start (or supersede) a device-code flow, and expose a pollable status.
/// </summary>
/// <remarks>
///     The user code and verification URL come back immediately while the exchange completes in the background.
///     Distinct from the silent-only credential <see cref="Implementation.AzureFoundryChatClientFactory" /> builds for
///     chat sends: this owns the one-time interactive sign-in that produces the persisted authentication record.
/// </remarks>
public interface IEntraDeviceCodeSignInCoordinator
{
    /// <summary>
    ///     Starts (superseding any in-flight attempt) a device-code sign-in against the stored connection's tenant /
    ///     client / scope and returns once the user code is available.
    /// </summary>
    /// <remarks>
    ///     The exchange completes in the background; poll <see cref="GetStatus" />. Throws
    ///     <see cref="InvalidOperationException" /> when no Entra ID connection with a tenant id and client id is
    ///     stored.
    /// </remarks>
    Task<EntraDeviceCodeSignInHandle> StartAsync(CancellationToken cancellationToken);

    /// <summary>Returns the current sign-in status snapshot. Carries no token material.</summary>
    EntraDeviceCodeSignInStatus GetStatus();
}
