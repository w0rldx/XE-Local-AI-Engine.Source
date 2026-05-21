namespace XE_Local_AI_Engine.Client.Services.Auth;

using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

public static class LocalOperatorAuthorization
{
    public const string OperatorRole = "Operator";
    public const string AuthenticationType = "LocalOperator";
    public const string UserName = "local-operator";

    public static AuthenticationState CreateAuthenticationState()
    {
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.Name, UserName),
            new Claim(ClaimTypes.Role, OperatorRole)
        ], AuthenticationType);

        return new AuthenticationState(new ClaimsPrincipal(identity));
    }
}
