namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

/// <summary>
///     Response to <c>POST cloud-settings/entra/device-code/start</c>: the user code and verification URL the
///     operator enters in a browser (any device). The exchange completes in the background; the UI polls
///     <see cref="EntraDeviceCodeSignInStatusResponse" />. Carries no token material.
/// </summary>
public sealed record EntraDeviceCodeSignInResponse
{
    /// <summary>The short code the operator enters at <see cref="VerificationUri" />.</summary>
    public required string UserCode { get; init; }

    /// <summary>The URL the operator opens to enter the code.</summary>
    public required string VerificationUri { get; init; }

    /// <summary>When the device code itself expires (not the resulting sign-in session).</summary>
    public required DateTimeOffset ExpiresAtUtc { get; init; }
}

/// <summary>
///     Response to <c>GET cloud-settings/entra/device-code/status</c>: the current Entra ID device-code sign-in
///     state. Carries no token material — only lifecycle state and, while pending, the same non-secret user code /
///     verification URL returned by the start endpoint.
/// </summary>
public sealed record EntraDeviceCodeSignInStatusResponse
{
    /// <summary>The sign-in lifecycle state, as the <c>EntraDeviceCodeSignInState</c> enum name.</summary>
    public required string State { get; init; }

    /// <summary>The short code, when a sign-in is pending.</summary>
    public string? UserCode { get; init; }

    /// <summary>The verification URL, when a sign-in is pending.</summary>
    public string? VerificationUri { get; init; }

    /// <summary>When the device code itself expires, when a sign-in is pending.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; init; }
}
