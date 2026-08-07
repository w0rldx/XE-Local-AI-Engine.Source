namespace XE_Local_AI_Engine.Tests.Auth;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Endpoints.Auth.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Coverage for the local "forgot password" recovery path: <see cref="INodeAuthService.ResetAdminPasswordAsync" />,
///     which the operator-run <c>--reset-admin-password</c> CLI calls to set a new admin password without the old one.
///     Exercised at the service layer (the CLI branch in Program.cs is a thin scope+resolve+exit wrapper over this).
/// </summary>
public sealed class NodeAuthPasswordResetTests
{
    private const string Email = "admin@example.test";
    private const string OldPassword = "Str0ng!Password123";
    private const string NewPassword = "R3set!Password456";

    [Test]
    public async Task Reset_WhenAdminExists_ReplacesPasswordSoOnlyTheNewOneLogsIn()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        await SetupAsync(client).ConfigureAwait(false);

        var result = await ResetAsync(factory, NewPassword).ConfigureAwait(false);
        AssertEx.True(result.Succeeded, "Reset must succeed for an existing admin with a policy-compliant password.");

        using var oldLogin = await LoginAsync(client, OldPassword).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        using var newLogin = await LoginAsync(client, NewPassword).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Test]
    public async Task Reset_WhenNoAdminExists_FailsAndDoesNotThrow()
    {
        await using var factory = new TestingWebAppFactory();

        var result = await ResetAsync(factory, NewPassword).ConfigureAwait(false);

        AssertEx.False(result.Succeeded, "With no completed setup there is no account to reset.");
        AssertEx.NotEmpty(result.Errors);
    }

    [Test]
    public async Task Reset_WhenNewPasswordViolatesPolicy_RollsBackAndKeepsOldPasswordWorking()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        await SetupAsync(client).ConfigureAwait(false);

        // "short" is under the 12-char policy: AddPassword fails after RemovePassword. The serializable transaction must
        // roll back so the account is NOT left passwordless — the original password must still authenticate.
        var result = await ResetAsync(factory, "short").ConfigureAwait(false);
        AssertEx.False(result.Succeeded);

        using var oldLogin = await LoginAsync(client, OldPassword).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, oldLogin.StatusCode);
    }

    [Test]
    public async Task Reset_RevokesExistingRefreshTokens()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        await SetupAsync(client).ConfigureAwait(false);

        using var loginResponse = await LoginAsync(client, OldPassword).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var refreshCookie = GetRefreshCookie(loginResponse);

        var result = await ResetAsync(factory, NewPassword).ConfigureAwait(false);
        AssertEx.True(result.Succeeded);

        // The pre-reset refresh token must no longer mint access tokens: an operator who reset the password logs every
        // stale session out.
        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/refresh");
        refreshRequest.Headers.Add("Cookie", refreshCookie);
        using var refreshResponse = await client.SendAsync(refreshRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, refreshResponse.StatusCode);
    }

    private static async Task<NodePasswordChangeResult> ResetAsync(TestingWebAppFactory factory, string newPassword)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var authService = scope.ServiceProvider.GetRequiredService<INodeAuthService>();
        return await authService.ResetAdminPasswordAsync(newPassword, CancellationToken.None).ConfigureAwait(false);
    }

    private static Task<HttpResponseMessage> SetupAsync(HttpClient client)
    {
        return client.PostAsJsonAsync("/api/local/v1/auth/setup",
            new
            {
                email = Email,
                password = OldPassword
            });
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string password)
    {
        return client.PostAsJsonAsync("/api/local/v1/auth/login",
            new
            {
                password
            });
    }

    private static string GetRefreshCookie(HttpResponseMessage response)
    {
        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(header => header.StartsWith($"{NodeAuthCookie.RefreshCookieName}=", StringComparison.Ordinal))
            : null;

        return AssertEx.NotNull(setCookie).Split(separator: ';', count: 2)[0];
    }
}
