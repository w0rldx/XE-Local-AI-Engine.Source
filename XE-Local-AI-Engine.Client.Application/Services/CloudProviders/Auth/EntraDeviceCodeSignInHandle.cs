namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

/// <summary>
///     The immediate result of starting an Entra ID device-code sign-in: the user code and verification URL the
///     operator enters in a browser (any device). Contains no secrets — the token exchange completes in the
///     background and is observed via <see cref="IEntraDeviceCodeSignInCoordinator.GetStatus" />.
/// </summary>
public sealed record EntraDeviceCodeSignInHandle(string UserCode, string VerificationUri, DateTimeOffset ExpiresAtUtc);
