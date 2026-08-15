namespace XE_Local_AI_Engine.Tests.Endpoints.NodeBinding.V1;

using System.Net;
using System.Net.Http.Json;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.NodeBinding.V1;
using XE_Local_AI_Engine.Client.Models.NodeBinding;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>POST binding/poll</c>: operator-gated, 200 with the terminal status, 400 on a platform refusal, and — the
///     interesting one — a poll cancelled by a concurrent <c>binding/cancel</c> still answers 200 with
///     <c>status=cancelled</c> rather than surfacing the OperationCanceledException as a 500.
/// </summary>
public sealed class PollNodeBindingEndpointTests
{
    private const string Route = "/api/local/v1/binding/poll";

    [Test]
    public async Task Poll_WhenAnonymous_Returns401()
    {
        await using var bindingService = NodeBindingEndpointHost.CreateService();
        await using var factory = NodeBindingEndpointHost.Create(bindingService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(PollRequest())
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Poll_WhenAuthenticatedButNotOperator_Returns403()
    {
        await using var bindingService = NodeBindingEndpointHost.CreateService();
        await using var factory = NodeBindingEndpointHost.Create(bindingService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(PollRequest())
        };
        factory.AddNonOperatorBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task Poll_WhenApproved_Returns200WithTerminalStatus()
    {
        await using var bindingService = NodeBindingEndpointHost.CreateService();
        bindingService.PollUntilTerminalAsync(Arg.Any<NodeBindingSession>(), Arg.Any<CancellationToken>())
                      .Returns(new PollNodeBindingResponse
                      {
                          Status = "approved",
                          IntervalSeconds = 7
                      });

        await using var factory = NodeBindingEndpointHost.Create(bindingService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(PollRequest())
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var poll = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<PollNodeBindingSessionResponse>().ConfigureAwait(false));
        AssertEx.Equal("approved", poll.Status);
        AssertEx.Equal(7, poll.IntervalSeconds);
    }

    [Test]
    public async Task Poll_WhenCancelledByOperator_Returns200WithCancelledStatus()
    {
        await using var bindingService = NodeBindingEndpointHost.CreateService();
        bindingService.PollUntilTerminalAsync(Arg.Any<NodeBindingSession>(), Arg.Any<CancellationToken>())
                      .Returns<Task<PollNodeBindingResponse>>(_ => throw new OperationCanceledException());

        await using var factory = NodeBindingEndpointHost.Create(bindingService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(PollRequest())
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        // The endpoint distinguishes "the operator cancelled" from "the caller disconnected": only the former is
        // reported as a normal 200 terminal state.
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var poll = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<PollNodeBindingSessionResponse>().ConfigureAwait(false));
        AssertEx.Equal("cancelled", poll.Status);
        AssertEx.Equal(3, poll.IntervalSeconds, "A cancelled poll must echo the request's interval back.");
    }

    [Test]
    public async Task Poll_WhenPlatformRefuses_Returns400NotServerError()
    {
        await using var bindingService = NodeBindingEndpointHost.CreateService();
        bindingService.PollUntilTerminalAsync(Arg.Any<NodeBindingSession>(), Arg.Any<CancellationToken>())
                      .Returns<Task<PollNodeBindingResponse>>(_ => throw new NodeBindingException("Central Platform returned an unknown binding status."));

        await using var factory = NodeBindingEndpointHost.Create(bindingService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(PollRequest())
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.Contains(body, "unknown binding status", StringComparison.Ordinal);
    }

    private static object PollRequest()
    {
        return new
        {
            deviceCode = "device-code-1",
            userCode = "USER-CODE",
            verificationUri = "https://central.example.test/device",
            verificationUriComplete = "https://central.example.test/device?code=USER-CODE",
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            intervalSeconds = 3
        };
    }
}
