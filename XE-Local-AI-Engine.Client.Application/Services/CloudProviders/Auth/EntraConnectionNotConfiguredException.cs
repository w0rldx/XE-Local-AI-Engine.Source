namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

/// <summary>
///     Thrown when an Entra ID sign-in flow is started but no matching Entra ID connection is configured in the stored
///     Cloud Settings (missing tenant id / client id / client secret for the selected sign-in method). This is a
///     user-actionable precondition — the operator must save Cloud Settings first — so the sign-in endpoints surface it
///     as a 400 validation error carrying this path-free, user-safe message. Any OTHER failure from the sign-in flow
///     (e.g. a busy redirect port, or an unexpected fault) is left to the global exception handlers, which return a
///     clean 500 instead of leaking the raw message. Derives from <see cref="InvalidOperationException" /> so the
///     precondition keeps its prior base type.
/// </summary>
public sealed class EntraConnectionNotConfiguredException(string message) : InvalidOperationException(message);
