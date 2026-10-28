namespace XE_Local_AI_Engine.Tests.Auth;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>An open hub connection ends with its sign-in: revoking a session, signing out everywhere or changing the password closes it.</summary>
/// <remarks>
///     The JWT events check a hub token only at the handshake. Real host, real JWT pipeline, real SignalR client; the
///     sign-outs go through the REST endpoints.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class NodeHubSignOutTests
{
    private const string Email = "admin@example.test";
    private const string Password = "Str0ng!Password123";

    [Test]
    public async Task RevokingASession_ClosesItsHubConnection_AndLeavesTheOtherSessionsConnectionUp()
    {
        await using var factory = new TestServerWebAppFactory();
        var first = await SetupAndLoginAsync(factory);
        var second = await LoginAsync(factory);
        await using var firstHub = await OpenChatHubAsync(factory, first);
        await using var secondHub = await OpenChatHubAsync(factory, second);

        AssertEx.Equal(HttpStatusCode.NoContent, await SendAsync(factory, second, HttpMethod.Delete, $"/api/local/v1/auth/sessions/{SessionId(first)}"));

        await firstHub.Closed.WaitAsync(TestBudgets.Contended);
        _ = await AssertEx.ThrowsAsync<Exception>(() => ProbeAsync(firstHub.Connection), "A closed connection takes no further invocation.");
        await ProbeAsync(secondHub.Connection);
        AssertEx.Equal(HubConnectionState.Connected, secondHub.Connection.State, "The other session's connection stays up.");
    }

    [Test]
    public async Task SigningOutEverywhere_ClosesEveryHubConnectionOfTheUser()
    {
        await using var factory = new TestServerWebAppFactory();
        var first = await SetupAndLoginAsync(factory);
        var second = await LoginAsync(factory);
        await using var firstHub = await OpenChatHubAsync(factory, first);
        await using var secondHub = await OpenChatHubAsync(factory, second);

        AssertEx.Equal(HttpStatusCode.NoContent, await SendAsync(factory, first, HttpMethod.Post, "/api/local/v1/auth/sessions/revoke-all"));

        await firstHub.Closed.WaitAsync(TestBudgets.Contended);
        await secondHub.Closed.WaitAsync(TestBudgets.Contended);
    }

    [Test]
    public async Task ChangingThePassword_ClosesAConnectionWhoseTokenNamesNoSession()
    {
        await using var factory = new TestServerWebAppFactory();
        var login = await SetupAndLoginAsync(factory);
        await using var legacyHub = await OpenHubAsync(factory, LocalApiRoutes.LocalChat.Hub, await MintLegacyAccessTokenAsync(factory));

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/change-password")
        {
            Content = JsonContent.Create(new
            {
                currentPassword = Password,
                newPassword = Password + "Changed"
            })
        };
        request.Headers.Authorization = Bearer(login.AccessToken);
        using var response = await client.SendAsync(request);
        AssertEx.True(response.IsSuccessStatusCode, $"The password change must succeed, got {response.StatusCode}.");

        await legacyHub.Closed.WaitAsync(TestBudgets.Contended);
    }

    [Test]
    public async Task AnInvocation_OnAConnectionWhoseSessionEndedWithoutAnAbort_IsRefused()
    {
        // The abort at sign-out is switched off, so only the per-invocation check stands between the connection and the hub.
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<INodeLiveConnectionRevoker>();
                services.AddSingleton(Substitute.For<INodeLiveConnectionRevoker>());
            }
        };
        var first = await SetupAndLoginAsync(factory);
        var second = await LoginAsync(factory);
        await using var chatHub = await OpenChatHubAsync(factory, first);
        await using var benchmarkHub = await OpenHubAsync(factory, LocalApiRoutes.Benchmarks.Hub, first.AccessToken);
        await ProbeAsync(chatHub.Connection);
        await benchmarkHub.Connection.InvokeAsync("Unsubscribe", Guid.NewGuid());

        AssertEx.Equal(HttpStatusCode.NoContent, await SendAsync(factory, second, HttpMethod.Delete, $"/api/local/v1/auth/sessions/{SessionId(first)}"));
        AssertEx.Equal(HubConnectionState.Connected, benchmarkHub.Connection.State, "With the abort off, the connection is still open.");

        _ = await AssertEx.ThrowsAsync<Exception>(() => ProbeAsync(chatHub.Connection), "A streaming invocation of an ended session is refused.");
        _ = await AssertEx.ThrowsAsync<Exception>(() => benchmarkHub.Connection.InvokeAsync("Unsubscribe", Guid.NewGuid()),
            "A plain invocation of an ended session is refused.");
        await chatHub.Closed.WaitAsync(TestBudgets.Contended);
        await benchmarkHub.Closed.WaitAsync(TestBudgets.Contended);
    }

    /// <summary>A streaming invocation that answers an empty stream for a conversation with nothing running.</summary>
    private static async Task ProbeAsync(HubConnection connection)
    {
        var events = await connection.StreamAsync<ChatStreamEvent>("ResumeConversation", Guid.NewGuid()).ToListAsync();
        AssertEx.Equal(0, events.Count, "No turn is running, so the resume stream must be empty.");
    }

    private static Task<OpenHub> OpenChatHubAsync(TestServerWebAppFactory factory, NodeAuthTokenResult session) =>
        OpenHubAsync(factory, LocalApiRoutes.LocalChat.Hub, session.AccessToken);

    private static async Task<OpenHub> OpenHubAsync(TestServerWebAppFactory factory, string route, string? accessToken)
    {
        var connection = new HubConnectionBuilder()
                         .WithUrl("http://localhost" + route, options =>
                         {
                             // WebSockets, as the browser uses: one authenticated upgrade, then no request the JWT events see.
                             // Long polling or SSE would re-authenticate every poll or send and close a revoked connection anyway.
                             options.SkipNegotiation = true;
                             options.Transports = HttpTransportType.WebSockets;
                             options.WebSocketFactory = async (context, cancellationToken) =>
                             {
                                 var client = factory.Server.CreateWebSocketClient();
                                 client.ConfigureRequest = request => request.Headers.Origin = "http://localhost";
                                 var uri = QueryHelpers.AddQueryString(context.Uri.ToString(), "access_token", AssertEx.NotNull(accessToken));
                                 return await client.ConnectAsync(new Uri(uri), cancellationToken);
                             };
                         })
                         .WithNodeJsonProtocol()
                         .Build();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        await connection.StartAndAwaitRegistrationAsync();
        return new OpenHub
        {
            Connection = connection,
            Closed = closed.Task
        };
    }

    private static async Task<NodeAuthTokenResult> SetupAndLoginAsync(TestServerWebAppFactory factory)
    {
        using (var client = factory.CreateClient())
        {
            using var response = await client.PostAsJsonAsync("/api/local/v1/auth/setup",
                new
                {
                    email = Email,
                    password = Password
                });
            AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        return await LoginAsync(factory);
    }

    private static async Task<NodeAuthTokenResult> LoginAsync(TestServerWebAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var login = await scope.ServiceProvider.GetRequiredService<INodeAuthService>()
                               .LoginAsync(Email, Password, clientLabel: null, CancellationToken.None);
        AssertEx.True(login.Succeeded, "The fixture's login must succeed before any hub connection is opened.");
        return login;
    }

    /// <summary>Mints an access token without <c>sid</c>, as one issued before sessions existed.</summary>
    private static async Task<string> MintLegacyAccessTokenAsync(TestServerWebAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var admin = AssertEx.NotNull(await scope.ServiceProvider.GetRequiredService<INodeIdentityStore>()
                                                .FindCompletedSetupUserAsync(CancellationToken.None));
        return scope.ServiceProvider.GetRequiredService<INodeTokenService>()
                    .CreateAccessToken(admin, [NodeAuthorizationPolicies.AdminRole]).AccessToken;
    }

    private static async Task<HttpStatusCode> SendAsync(TestServerWebAppFactory factory, NodeAuthTokenResult caller, HttpMethod method, string route)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(method, route);
        request.Headers.Authorization = Bearer(caller.AccessToken);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static string SessionId(NodeAuthTokenResult result) =>
        new JsonWebToken(AssertEx.NotNull(result.AccessToken)).GetClaim(JwtRegisteredClaimNames.Sid).Value;

    private static AuthenticationHeaderValue Bearer(string? accessToken) =>
        new("Bearer", AssertEx.NotNull(accessToken));

    /// <summary>An open connection and a task that completes when the connection closes.</summary>
    private sealed class OpenHub : IAsyncDisposable
    {
        public required HubConnection Connection { get; init; }

        public required Task Closed { get; init; }

        public ValueTask DisposeAsync() =>
            Connection.DisposeAsync();
    }
}
