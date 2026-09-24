namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

/// <summary>The state of the most recent / current Entra ID device-code sign-in attempt.</summary>
public enum EntraDeviceCodeSignInState
{
    /// <summary>No sign-in has been started this process lifetime.</summary>
    None,

    /// <summary>A sign-in is in flight: the user code + verification URL are available and completion is awaited.</summary>
    Pending,

    /// <summary>The most recent sign-in completed and persisted an authentication record.</summary>
    Succeeded,

    /// <summary>The most recent sign-in failed (timed out, was superseded, or the exchange errored).</summary>
    Failed
}

/// <summary>
///     An immutable snapshot of the current Entra ID device-code sign-in state for the status endpoint. Carries no
///     token material.
/// </summary>
public sealed class EntraDeviceCodeSignInStatus
{
    public required EntraDeviceCodeSignInState State { get; init; }

    public required string? UserCode { get; init; }

    public required string? VerificationUri { get; init; }

    public required DateTimeOffset? ExpiresAtUtc { get; init; }

    /// <summary>Idle status used before any sign-in has been attempted.</summary>
    public static EntraDeviceCodeSignInStatus None { get; } = new()
    {
        State = EntraDeviceCodeSignInState.None,
        UserCode = null,
        VerificationUri = null,
        ExpiresAtUtc = null
    };

    /// <summary>Terminal status after a sign-in succeeded and persisted a record.</summary>
    public static EntraDeviceCodeSignInStatus Succeeded { get; } = new()
    {
        State = EntraDeviceCodeSignInState.Succeeded,
        UserCode = null,
        VerificationUri = null,
        ExpiresAtUtc = null
    };

    /// <summary>Terminal status after a sign-in failed, timed out, or was superseded.</summary>
    public static EntraDeviceCodeSignInStatus Failed { get; } = new()
    {
        State = EntraDeviceCodeSignInState.Failed,
        UserCode = null,
        VerificationUri = null,
        ExpiresAtUtc = null
    };

    /// <summary>Builds the in-flight status carrying the user-facing code and verification URL.</summary>
    public static EntraDeviceCodeSignInStatus Pending(string userCode, string verificationUri, DateTimeOffset expiresAtUtc)
    {
        return new EntraDeviceCodeSignInStatus
        {
            State = EntraDeviceCodeSignInState.Pending,
            UserCode = userCode,
            VerificationUri = verificationUri,
            ExpiresAtUtc = expiresAtUtc
        };
    }
}
