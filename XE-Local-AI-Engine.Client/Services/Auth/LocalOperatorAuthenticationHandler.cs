namespace XE_Local_AI_Engine.Client.Services.Auth;

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

public sealed class LocalOperatorAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly ILocalOperatorTokenProvider _tokenProvider;

    public LocalOperatorAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ILocalOperatorTokenProvider tokenProvider)
        : base(options, logger, encoder)
    {
        _tokenProvider = tokenProvider;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(LocalOperatorAuthorization.HeaderName, out var submittedValues))
        {
            return Task.FromResult(AuthenticateResult.Fail("Missing local operator token."));
        }

        var submittedToken = submittedValues.Count == 1 ? submittedValues[0] : null;
        if (string.IsNullOrWhiteSpace(submittedToken) || !TokenMatches(submittedToken, _tokenProvider.Token))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid local operator token."));
        }

        var ticket = new AuthenticationTicket(LocalOperatorAuthorization.CreatePrincipal(), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static bool TokenMatches(string submittedToken, string expectedToken)
    {
        var submittedBytes = Encoding.UTF8.GetBytes(submittedToken);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);

        return submittedBytes.Length == expectedBytes.Length
               && CryptographicOperations.FixedTimeEquals(submittedBytes, expectedBytes);
    }
}
