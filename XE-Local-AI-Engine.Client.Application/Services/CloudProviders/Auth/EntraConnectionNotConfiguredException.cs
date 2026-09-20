namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

/// <summary>
///     Thrown when an Entra ID sign-in flow starts but the stored Cloud Settings carry no matching Entra ID connection
///     (tenant id / client id / client secret for the selected sign-in method).
/// </summary>
/// <remarks>
///     A user-actionable precondition — the operator must save Cloud Settings first — so the sign-in endpoints surface
///     it as a 400 validation error carrying this path-free, user-safe message. Any OTHER sign-in failure (a busy
///     redirect port, an unexpected fault) is left to the global exception handlers, which return a clean 500 rather
///     than leaking the raw message. Derives from <see cref="InvalidOperationException" /> so the precondition keeps
///     its prior base type.
/// </remarks>
public sealed class EntraConnectionNotConfiguredException : InvalidOperationException
{
    public EntraConnectionNotConfiguredException(string message) : base(message)
    {
    }
}
