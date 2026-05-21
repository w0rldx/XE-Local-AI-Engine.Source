namespace XE_Local_AI_Engine.Client.Services.Auth;

using Microsoft.AspNetCore.Components.Authorization;

public sealed class LocalOperatorAuthenticationStateProvider : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        return Task.FromResult(LocalOperatorAuthorization.CreateAuthenticationState());
    }
}
