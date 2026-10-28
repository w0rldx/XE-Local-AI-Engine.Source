namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

/// <summary>
///     One-shot result of <see cref="LoopbackAuthorizationCodeListener.WaitForCallbackAsync" />. Never carries a raw
///     query string, only the specific fields the caller needs.
/// </summary>
/// <remarks>
///     AAD's error text (RFC 6749 §4.1.2.1 <c>error</c> / <c>error_description</c>) is truncated to a single line
///     before it is ever logged; those are operator-facing diagnostics, not attacker-controlled HTML, and the
///     callback page never reflects them regardless — see <see cref="LoopbackAuthorizationCodeListener" />.
/// </remarks>
internal sealed class LoopbackCallbackResult
{
    public required LoopbackCallbackOutcome Outcome { get; init; }

    public string? AuthorizationCode { get; init; }

    public string? SanitizedError { get; init; }

    public string? SanitizedErrorDescription { get; init; }

    public static LoopbackCallbackResult Success(string code)
    {
        return new LoopbackCallbackResult
        {
            Outcome = LoopbackCallbackOutcome.Success,
            AuthorizationCode = code
        };
    }

    public static readonly LoopbackCallbackResult StateMismatch = new()
    {
        Outcome = LoopbackCallbackOutcome.StateMismatch
    };

    public static readonly LoopbackCallbackResult MissingCode = new()
    {
        Outcome = LoopbackCallbackOutcome.MissingCode
    };

    public static readonly LoopbackCallbackResult TimedOut = new()
    {
        Outcome = LoopbackCallbackOutcome.TimedOut
    };

    public static LoopbackCallbackResult AadError(string? error, string? errorDescription)
    {
        return new LoopbackCallbackResult
        {
            Outcome = LoopbackCallbackOutcome.AadError,
            SanitizedError = error,
            SanitizedErrorDescription = errorDescription
        };
    }
}
