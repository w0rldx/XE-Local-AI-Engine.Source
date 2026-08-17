namespace XE_Local_AI_Engine.Tests.Endpoints.NodeBinding.V1;

using System.Net;
using System.Net.Http.Json;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.NodeBinding.V1;
using XE_Local_AI_Engine.Client.Models.NodeBinding;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>POST binding/start</c>: operator-gated, 200 with the device-code session mapped to its wire shape, and 400
///     (FastEndpoints error envelope) when the Central Platform refuses — never a 500.
/// </summary>
public sealed class StartNodeBindingEndpointTests
{
    private const string Route = "/api/local/v1/binding/start";

    [Test]
    public async Task Start_WhenAnonymous_Returns401()
    {
        await using var bindingService = NodeBindingEndpointHost.CreateService();
        await using var factory = NodeBindingEndpointHost.Create(bindingService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Start_WhenAuthenticatedButNotOperator_Returns403()
    {
        await using var bindingService = NodeBindingEndpointHost.CreateService();
        await using var factory = NodeBindingEndpointHost.Create(bindingService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route);
        factory.AddNonOperatorBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task Start_WhenSessionOpened_Returns200WithWireStatus()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        await using var bindingService = NodeBindingEndpointHost.CreateService();
        bindingService.StartBindingAsync(Arg.Any<CancellationToken>()).Returns(new NodeBindingSession
        {
            DeviceCode = "device-code-1",
            UserCode = "USER-CODE",
            VerificationUri = "https://central.example.test/device",
            VerificationUriComplete = "https://central.example.test/device?code=USER-CODE",
            ExpiresAt = expiresAt,
            IntervalSeconds = 5,
            Status = NodeBindingStatus.Pending
        });

        await using var factory = NodeBindingEndpointHost.Create(bindingService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var session = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<NodeBindingSessionResponse>().ConfigureAwait(false));
        AssertEx.Equal("device-code-1", session.DeviceCode);
        AssertEx.Equal("USER-CODE", session.UserCode);
        AssertEx.Equal("https://central.example.test/device?code=USER-CODE", session.VerificationUriComplete);
        AssertEx.Equal(5, session.IntervalSeconds);

        // The mapper projects the enum onto a lowercase wire token the React client switches on.
        AssertEx.Equal("pending", session.Status);
    }

    [Test]
    public async Task Start_WhenPlatformRefuses_Returns400NotServerError()
    {
        await using var bindingService = NodeBindingEndpointHost.CreateService();
        bindingService.StartBindingAsync(Arg.Any<CancellationToken>())
                      .Returns<Task<NodeBindingSession>>(_ => throw new NodeBindingException("Central Platform rejected the binding request."));

        await using var factory = NodeBindingEndpointHost.Create(bindingService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.Contains(body, "Central Platform rejected the binding request", StringComparison.Ordinal);
    }
}
