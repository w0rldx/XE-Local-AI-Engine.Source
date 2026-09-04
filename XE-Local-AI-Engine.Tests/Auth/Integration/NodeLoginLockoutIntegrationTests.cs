namespace XE_Local_AI_Engine.Tests.Auth.Integration;

using System.Net;
using System.Net.Http.Json;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeLoginLockoutIntegrationTests
{
    private const string Email = "admin@example.test";
    private const string Password = "Str0ng!Password123";
    private const string WrongPassword = "wrong-password";

    // Identity lockout policy configured in ConfigureServices: MaxFailedAccessAttempts = 5, DefaultLockoutTimeSpan = 5 min.
    private const int LockoutThreshold = 5;

    private const int LockoutSeconds = 300;

    [Test]
    public async Task Login_WhenPasswordIsWrong_ReturnsUnauthorized()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        await SetupAsync(client).ConfigureAwait(false);

        using var response = await LoginAsync(client, WrongPassword).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The enumeration boundary: before the threshold a failure must stay indistinguishable from "no such account", so
    // the body carries no `code` and no Retry-After tells a caller anything about this account.
    [Test]
    public async Task Login_WhenPasswordIsWrongBeforeThreshold_ReturnsUnauthorizedWithoutACode()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        await SetupAsync(client).ConfigureAwait(false);

        using var response = await LoginAsync(client, WrongPassword).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertEx.False(response.Headers.Contains("Retry-After"), "A pre-threshold failure must not carry Retry-After.");

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.False(body.Contains("locked-out", StringComparison.Ordinal),
            $"A pre-threshold failure must not name a lockout. Body: '{body}'.");
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

        // The whole point of the coded body: the correct password is being refused, and only `code` explains why.
        var payload = AssertEx.NotNull(await lockedOutResponse.Content.ReadFromJsonAsync<LockedOutBody>().ConfigureAwait(false));
        AssertEx.Equal("locked-out", payload.Code);
        AssertEx.NotEmpty(payload.Message);
        AssertEx.True(payload.RetryAfterSeconds is > 0 and <= LockoutSeconds,
            $"retryAfterSeconds must fall inside the 5-minute lockout window but was {payload.RetryAfterSeconds}.");

        // The header repeats the body's number so a non-browser caller does not have to parse the body to back off.
        var retryAfter = AssertEx.NotNull(lockedOutResponse.Headers.RetryAfter, "The locked-out 401 must carry a Retry-After header.");
        AssertEx.Equal(TimeSpan.FromSeconds(payload.RetryAfterSeconds), retryAfter.Delta ?? TimeSpan.Zero);
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

    private sealed record LockedOutBody(string Message, string Code, int RetryAfterSeconds);
}
