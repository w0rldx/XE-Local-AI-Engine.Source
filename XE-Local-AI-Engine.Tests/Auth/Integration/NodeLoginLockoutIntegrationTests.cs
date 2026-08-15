namespace XE_Local_AI_Engine.Tests.Auth.Integration;

using System.Net;
using System.Net.Http.Json;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeLoginLockoutIntegrationTests
{
    private const string Email = "admin@example.test";
    private const string Password = "Str0ng!Password123";
    private const string WrongPassword = "wrong-password";

    // Identity lockout policy configured in ConfigureServices: MaxFailedAccessAttempts = 5.
    private const int LockoutThreshold = 5;

    [Test]
    public async Task Login_WhenPasswordIsWrong_ReturnsUnauthorized()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        await SetupAsync(client).ConfigureAwait(false);

        using var response = await LoginAsync(client, WrongPassword).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Login_WhenFailedAttemptsReachThreshold_LocksAccountSoCorrectPasswordIsRejected()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        await SetupAsync(client).ConfigureAwait(false);

        for (var attempt = 0; attempt < LockoutThreshold; attempt++)
        {
            using var failedResponse = await LoginAsync(client, WrongPassword).ConfigureAwait(false);
            AssertEx.Equal(HttpStatusCode.Unauthorized, failedResponse.StatusCode);
        }

        using var lockedOutResponse = await LoginAsync(client, Password).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, lockedOutResponse.StatusCode);
    }

    [Test]
    public async Task Login_WhenCorrectPasswordBeforeThreshold_Succeeds()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        await SetupAsync(client).ConfigureAwait(false);

        for (var attempt = 0; attempt < LockoutThreshold - 1; attempt++)
        {
            using var failedResponse = await LoginAsync(client, WrongPassword).ConfigureAwait(false);
            AssertEx.Equal(HttpStatusCode.Unauthorized, failedResponse.StatusCode);
        }

        using var successResponse = await LoginAsync(client, Password).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, successResponse.StatusCode);
    }

    private static async Task SetupAsync(HttpClient client)
    {
        using var setupResponse = await client.PostAsJsonAsync("/api/local/v1/auth/setup",
            new
            {
                email = Email,
                password = Password
            }).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NoContent, setupResponse.StatusCode);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string password)
    {
        return client.PostAsJsonAsync("/api/local/v1/auth/login",
            new
            {
                password
            });
    }
}
