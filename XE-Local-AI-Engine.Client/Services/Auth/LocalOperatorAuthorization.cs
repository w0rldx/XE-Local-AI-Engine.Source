namespace XE_Local_AI_Engine.Client.Services.Auth;

using System.Security.Claims;

public static class LocalOperatorAuthorization
{
    public const string OperatorRole = "Operator";
    public const string AuthenticationType = "LocalOperator";
    public const string OperatorPolicy = "LocalOperatorOnly";
    public const string HeaderName = "X-Local-Operator";
    public const string UserName = "local-operator";

    public static ClaimsPrincipal CreatePrincipal()
    {
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.Name, UserName),
            new Claim(ClaimTypes.Role, OperatorRole)
        ], AuthenticationType);

        return new ClaimsPrincipal(identity);
    }
}
