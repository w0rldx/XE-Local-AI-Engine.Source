namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

/// <summary>The state of the most recent / current Entra ID authorization-code sign-in attempt.</summary>
public enum EntraAuthCodeSignInState
{
    /// <summary>No sign-in has been started this process lifetime.</summary>
    None,

    /// <summary>A sign-in is in flight: the authorize URL was returned and the loopback callback is awaited.</summary>
    Pending,

    /// <summary>The most recent sign-in completed and persisted a delegated credential.</summary>
    Succeeded,

    /// <summary>The most recent sign-in failed (timed out, was superseded, or the exchange errored).</summary>
    Failed
}

/// <summary>
///     An immutable snapshot of the current Entra ID authorization-code sign-in state for the status endpoint.
///     Carries no token material and no AAD error detail (logged server-side only, never surfaced to the UI).
/// </summary>
public sealed record EntraAuthCodeSignInStatus(EntraAuthCodeSignInState State, DateTimeOffset? ExpiresAtUtc)
{
    /// <summary>Idle status used before any sign-in has been attempted.</summary>
    public static EntraAuthCodeSignInStatus None { get; } = new(EntraAuthCodeSignInState.None, null);

    /// <summary>Terminal status after a sign-in succeeded and persisted a delegated credential.</summary>
    public static EntraAuthCodeSignInStatus Succeeded { get; } = new(EntraAuthCodeSignInState.Succeeded, null);

    /// <summary>Terminal status after a sign-in failed, timed out, or was superseded.</summary>
    public static EntraAuthCodeSignInStatus Failed { get; } = new(EntraAuthCodeSignInState.Failed, null);

    /// <summary>Builds the in-flight status carrying when the pending attempt gives up waiting for the callback.</summary>
    public static EntraAuthCodeSignInStatus Pending(DateTimeOffset expiresAtUtc)
    {
        return new EntraAuthCodeSignInStatus(EntraAuthCodeSignInState.Pending, expiresAtUtc);
    }
}
